using System.CommandLine;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using DriftBuster.Backend;
using DriftBuster.Backend.Diff;
using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Models;
using DriftBuster.Backend.MultiServer;

namespace DriftBuster.Cli.Commands;

/// <summary>
/// <c>driftbuster multi-server</c>: reads the request JSON from stdin, runs
/// <see cref="MultiServerRunner"/> and writes newline-delimited JSON (<c>json.dumps(record, ensure_ascii=True)</c>): a
/// <c>{"type": "progress", "payload": {...}}</c> line per progress update, then <c>{"type": "result", "payload": response}</c> with exit
/// code 0, or <c>{"type": "error", "message": ...}</c> with exit code 1.
/// </summary>
internal static class MultiServerCommand
{
    // ConfigDrilldown.DiffSummary is a JsonElement the serializer writes as it holds it, without the string converter.
    private const string DiffSummaryKey = "diff_summary";

    private static readonly JsonSerializerOptions ModelJson = new()
    {
        Converters = { new JsonStringEnumMemberConverter(), new TimestampConverter(), new EscapedStringConverter() },
    };

    /// <summary><c>datetime.now(UTC).isoformat()</c> for every model timestamp.</summary>
    private sealed class TimestampConverter : JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.GetDateTimeOffset();

        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) => writer.WriteStringValue(DiffBuilder.IsoFormat(value));
    }

    /// <summary>
    /// Writes each string with "\" doubled and every unpaired surrogate as the text <c>\uXXXX</c>, which <see cref="Unescape"/> reverses once
    /// the document is read back (except inside <c>diff_summary</c>, a JSON element written as it is held), so a surrogate the serializer would replace with U+FFFD reaches <c>ensure_ascii</c> intact.
    /// </summary>
    private sealed class EscapedStringConverter : JsonConverter<string>
    {
        public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.GetString();

        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WriteStringValue(Escape(value));

        public override string ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.GetString()!;

        public override void WriteAsPropertyName(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WritePropertyName(Escape(value));
    }

    /// <summary>Emits every progress update as its line as soon as the runner reports it.</summary>
    private sealed class LineProgress(TextWriter stdout) : IProgress<ScanProgress>
    {
        public void Report(ScanProgress value) => EmitLine(stdout, new OrderedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["type"] = "progress",
            ["payload"] = new OrderedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["host_id"] = value.HostId,
                ["status"] = JsonSerializer.SerializeToElement(value.Status, ModelJson).GetString(),
                ["message"] = value.Message,
                ["timestamp"] = DiffBuilder.IsoFormat(value.Timestamp),
            },
        });
    }

    public static Command Build(Func<TextReader> stdin)
    {
        var command = new Command("multi-server", "Run a multi-server scan request read from stdin; newline-delimited JSON on stdout.");
        command.SetAction(parseResult => Execute(stdin(), parseResult.InvocationConfiguration.Output));
        return command;
    }

    /// <summary><c>main()</c>.</summary>
    internal static int Execute(TextReader stdin, TextWriter stdout)
    {
        try
        {
            var response = Run(stdin.ReadToEnd(), stdout);
            EmitLine(stdout, new OrderedDictionary<string, object?>(StringComparer.Ordinal) { ["type"] = "result", ["payload"] = ResponsePayload(response) });
            return 0;
        }
        catch (CommandExitException exc)
        {
            EmitError(stdout, exc.Message.Length > 0 ? exc.Message : "Request aborted");
            return 1;
        }
        catch (Exception exc) when (exc is not OutOfMemoryException)
        {
            EmitError(stdout, $"Unhandled error: {exc.Message}");
            return 1;
        }
    }

    private static ServerScanResponse Run(string raw, TextWriter stdout)
    {
        if (!EngineJson.TryLoadsOrRaiseLimits(raw, out var request))
        {
            throw new CommandExitException("Invalid JSON payload: invalid JSON document");
        }

        var schema = EngineBuiltins.Get(request, "schema_version");
        var schemaVersion = EngineBuiltins.IsTruthy(schema) ? EngineRepr.Str(schema) : MultiServerSchema.Version;
        if (!string.Equals(schemaVersion, MultiServerSchema.Version, StringComparison.Ordinal))
        {
            throw new CommandExitException($"Unsupported schema version: {schemaVersion}");
        }

        var cacheDir = DiffCache.ResolveCacheDirectory(CacheDirText(EngineBuiltins.Get(request, "cache_dir")), Environment.CurrentDirectory);
        var plans = MultiServerPlan.BuildPlans(request);
        return new MultiServerRunner(cacheDir).Run(plans, new LineProgress(stdout));
    }

    // `if cache_dir: Path(cache_dir)`: a truthy value that is not a str is refused.
    private static string? CacheDirText(object? value) => value switch
    {
        _ when !EngineBuiltins.IsTruthy(value) => null,
        string text => text,
        _ => throw new InvalidDataException(
            $"cache_dir must be a path string, not '{EngineBuiltins.TypeName(value)}'"),
    };

    private static void EmitError(TextWriter stdout, string message)
        => EmitLine(stdout, new OrderedDictionary<string, object?>(StringComparer.Ordinal) { ["type"] = "error", ["message"] = message });

    private static void EmitLine(TextWriter stdout, OrderedDictionary<string, object?> payload)
    {
        stdout.Write(Canonicaliser.Dumps(payload, indent: false, ensureAscii: true, sortKeys: false));
        stdout.Write('\n');
        stdout.Flush();
    }

    /// <summary>The response as the JSON value <c>MultiServerRunner.run</c> returns: the model serialised with Python's spellings, keys in model order.</summary>
    internal static object? ResponsePayload(ServerScanResponse response)
    {
        var text = JsonSerializer.Serialize(response, ModelJson);
        return EngineJson.TryLoads(text, out var parsed)
            ? Unescape(parsed)
            : throw new InvalidOperationException("The multi-server response did not serialise to JSON.");
    }

    private static string Escape(string value)
    {
        var builder = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            var ch = value[index];
            if (ch == '\\')
            {
                builder.Append(@"\\");
            }
            else if (char.IsSurrogate(ch) && !(char.IsHighSurrogate(ch) && index + 1 < value.Length && char.IsLowSurrogate(value[index + 1])))
            {
                builder.Append(CultureInfo.InvariantCulture, $"\\u{(int)ch:x4}");
            }
            else
            {
                builder.Append(ch);
                if (char.IsHighSurrogate(ch))
                {
                    builder.Append(value[++index]);
                }
            }
        }

        return builder.ToString();
    }

    private static object? Unescape(object? value) => value switch
    {
        string text => UnescapeText(text),
        OrderedDictionary<string, object?> mapping => new OrderedDictionary<string, object?>(
            mapping.Select(pair => KeyValuePair.Create(
                UnescapeText(pair.Key),
                string.Equals(pair.Key, DiffSummaryKey, StringComparison.Ordinal) ? pair.Value : Unescape(pair.Value))),
            StringComparer.Ordinal),
        List<object?> items => items.Select(Unescape).ToList(),
        _ => value,
    };

    private static string UnescapeText(string text)
    {
        if (!text.Contains('\\', StringComparison.Ordinal))
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] != '\\')
            {
                builder.Append(text[index]);
            }
            else if (text[index + 1] == '\\')
            {
                builder.Append('\\');
                index++;
            }
            else
            {
                builder.Append((char)int.Parse(text.AsSpan(index + 2, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                index += 5;
            }
        }

        return builder.ToString();
    }
}
