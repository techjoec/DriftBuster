using System.CommandLine;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

using DriftBuster.Backend;
using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Models;
using DriftBuster.Backend.MultiServer;

namespace DriftBuster.Cli;

/// <summary>The <c>multi-server</c> surface of <c>parity-dump</c> (py_dump.py <c>cmd_multi_server</c>).</summary>
public static partial class ParityDump
{
    private const string TimestampToken = "<timestamp>";

    private static readonly HashSet<string> TimestampKeys = new(StringComparer.Ordinal) { "timestamp", "last_updated", "last_seen", "generated_at" };

    private static readonly JsonSerializerOptions ModelJson = new()
    {
        Converters = { new JsonStringEnumMemberConverter(), new IsoFormatTimestampConverter(), new LoneSurrogateStringConverter() },
    };

    /// <summary>
    /// Writes every model string with its unpaired surrogates as the literal text <c>\uXXXX</c> (<see cref="CanonicalJson.EscapeLoneSurrogates"/>),
    /// the spelling py_dump.py gives them. The serializer would otherwise write U+FFFD for each one, before the dump could escape it.
    /// </summary>
    private sealed class LoneSurrogateStringConverter : JsonConverter<string>
    {
        public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.GetString();

        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
            => writer.WriteStringValue(CanonicalJson.EscapeLoneSurrogates(value));

        public override string ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.GetString()!;

        public override void WriteAsPropertyName(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
            => writer.WritePropertyName(CanonicalJson.EscapeLoneSurrogates(value));
    }

    /// <summary>
    /// Writes a model timestamp as <c>datetime.now(UTC).isoformat()</c> spells it (six fraction digits, none for a whole second,
    /// then <c>+00:00</c>) when the value is one such a call can return: offset zero and microsecond precision. Any other value is
    /// written in round-trip form, which <see cref="ReplaceTimestamps"/> then reports as an invalid timestamp.
    /// </summary>
    private sealed class IsoFormatTimestampConverter : JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.GetDateTimeOffset();

        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
            => writer.WriteStringValue(SpellTimestamp(value));
    }

    /// <summary>The spelling <see cref="IsoFormatTimestampConverter"/> writes for <paramref name="value"/>.</summary>
    internal static string SpellTimestamp(DateTimeOffset value)
        => value.Offset == TimeSpan.Zero && value.Ticks % 10 == 0
            ? DriftBuster.Backend.Diff.DiffBuilder.IsoFormat(value)
            : value.ToString("O", CultureInfo.InvariantCulture);

    /// <summary>Records every update on the reporting thread, as py_dump.py records every line <c>emit_progress</c> writes.</summary>
    private sealed class ListProgress(List<ScanProgress> updates) : IProgress<ScanProgress>
    {
        public void Report(ScanProgress value) => updates.Add(value);
    }

    private static Command BuildMultiServer()
    {
        var plansArgument = new Argument<string>("plans") { Description = "JSON request file holding a \"plans\" array." };
        var budgetOption = new Option<long?>("--sample-budget") { Description = "Aggregate sampling budget per host in bytes." };
        var sampleSizeOption = new Option<int?>("--sample-size") { Description = "Per-file sample size in bytes." };
        var runsOption = new Option<int>("--runs") { DefaultValueFactory = _ => 1, Description = "Runs against one cache; the last is dumped." };
        var command = new Command("multi-server", "MultiServerRunner progress, response and cache as one JSON document.");
        command.Arguments.Add(plansArgument);
        command.Options.Add(budgetOption);
        command.Options.Add(sampleSizeOption);
        command.Options.Add(runsOption);
        command.SetAction(parseResult =>
        {
            WriteLines(parseResult, [MultiServer(
                parseResult.GetValue(plansArgument)!,
                parseResult.GetValue(budgetOption),
                parseResult.GetValue(sampleSizeOption),
                parseResult.GetValue(runsOption))]);
            return 0;
        });
        return command;
    }

    /// <summary>
    /// <c>MultiServerRunner(cache, sampleBudget, sampleSize).Run(plans)</c> over a fresh temporary cache, <paramref name="runs"/>
    /// times, as <c>{"progress", "response", "cache", "key_order"}</c> for the last run: timestamps replaced by
    /// <c>&lt;timestamp&gt;</c> and absolute roots respelled relative to the working directory.
    /// </summary>
    internal static string MultiServer(string plansPath, long? sampleBudget, int? sampleSize, int runs)
    {
        var plans = ReadPlans(plansPath);
        var cache = Directory.CreateTempSubdirectory("driftbuster-parity-multi-server-");
        try
        {
            ServerScanResponse? response = null;
            var updates = new List<ScanProgress>();
            for (var run = 0; run < Math.Max(1, runs); run++)
            {
                updates = [];
                var runner = new MultiServerRunner(cache.FullName, sampleBudget, sampleSize);
                response = runner.Run(plans, new ListProgress(updates));
            }

            if (!PythonJson.TryLoads(JsonSerializer.Serialize(response, ModelJson), out var parsed) || parsed is not OrderedDictionary<string, object?> payload)
            {
                throw new InvalidOperationException("The response did not serialise to a JSON object.");
            }

            RespellRoots(payload);
            var record = new OrderedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["progress"] = updates.Select(ProgressEntry).ToList(),
                ["response"] = ReplaceTimestamps(payload),
                ["cache"] = CacheEntries(cache.FullName),
            };
            record["key_order"] = CanonicalJson.KeyOrder(record);
            return CanonicalJson.Serialize(record);
        }
        finally
        {
            cache.Delete(recursive: true);
        }
    }

    // py_dump.py: json.loads(Path(plans).read_text(encoding="utf-8")), then multi_server._build_plans(request) with Python's coercions.
    private static IReadOnlyList<MultiServerPlan> ReadPlans(string plansPath)
    {
        if (!PythonJson.TryLoads(StrictUtf8.GetString(File.ReadAllBytes(plansPath)), out var request))
        {
            throw new InvalidDataException($"The request file is not valid JSON: {plansPath}");
        }

        return MultiServerPlan.BuildPlans(request);
    }

    private static OrderedDictionary<string, object?> ProgressEntry(ScanProgress update) => new(StringComparer.Ordinal)
    {
        ["host_id"] = update.HostId,
        ["status"] = JsonSerializer.SerializeToElement(update.Status, ModelJson).GetString(),
        ["message"] = update.Message,
    };

    private static List<object?> CacheEntries(string cache)
    {
        var entries = new List<object?>();
        foreach (var file in Directory.GetFiles(cache).Order(Comparer<string>.Create(PathText.CompareCodePoints)))
        {
            var raw = File.ReadAllBytes(file);
            object? signature = null;
            if (PythonJson.TryLoads(StrictUtf8.GetString(raw), out var parsed) && parsed is IReadOnlyDictionary<string, object?> entry)
            {
                entry.TryGetValue("signature", out signature);
            }

            entries.Add(new OrderedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["file"] = Path.GetFileName(file),
                ["signature"] = signature,
                ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(raw)),
            });
        }

        return entries;
    }

    /// <summary>
    /// py_dump.py <c>_replace_timestamps</c>: every timestamp key's string value becomes the token when it is spelled as
    /// <c>datetime.now(UTC).isoformat()</c> spells it.
    /// </summary>
    internal static object? ReplaceTimestamps(object? value)
    {
        switch (value)
        {
            case OrderedDictionary<string, object?> mapping:
                var replaced = new OrderedDictionary<string, object?>(StringComparer.Ordinal);
                foreach (var (key, item) in mapping)
                {
                    replaced[key] = TimestampKeys.Contains(key) && item is string stamp
                        ? IsoUtc().IsMatch(stamp) ? TimestampToken : $"<invalid timestamp: {stamp}>"
                        : ReplaceTimestamps(item);
                }

                return replaced;
            case List<object?> items:
                return items.Select(ReplaceTimestamps).ToList();
            default:
                return value;
        }
    }

    /// <summary>py_dump.py <c>_respell_roots</c>: absolute roots become relative to the working directory, in roots and messages.</summary>
    internal static void RespellRoots(OrderedDictionary<string, object?> response)
    {
        if (!response.TryGetValue("results", out var value) || value is not List<object?> results)
        {
            return;
        }

        var mappings = results.OfType<OrderedDictionary<string, object?>>().ToList();
        var spelled = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var result in mappings)
        {
            foreach (var root in Roots(result).Where(Path.IsPathRooted))
            {
                spelled[root] = Path.GetRelativePath(Environment.CurrentDirectory, root);
            }
        }

        // One pass, longest root first at each position, so a respelled root is never rewritten again by a shorter one.
        var pattern = spelled.Count == 0
            ? null
            : new Regex(
                string.Join('|', spelled.Keys.OrderByDescending(root => root.Length).Select(Regex.Escape)),
                RegexOptions.CultureInvariant,
                TimeSpan.FromSeconds(2));
        foreach (var result in mappings)
        {
            result["roots"] = Roots(result).Select(root => (object?)(spelled.TryGetValue(root, out var relative) ? relative : root)).ToList();
            if (pattern is not null && result.TryGetValue("message", out var message) && message is string text)
            {
                result["message"] = pattern.Replace(text, match => spelled[match.Value]);
            }
        }
    }

    private static IEnumerable<string> Roots(OrderedDictionary<string, object?> result)
        => result.TryGetValue("roots", out var roots) && roots is List<object?> list ? list.OfType<string>() : [];

    // datetime.now(UTC).isoformat(): exactly six fraction digits or none, and "+00:00".
    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d{6})?\+00:00\z", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 2000)]
    private static partial Regex IsoUtc();
}
