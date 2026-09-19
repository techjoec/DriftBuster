using System.CommandLine;
using System.Text.Json;

using DriftBuster.Backend;
using DriftBuster.Backend.Models;
using DriftBuster.Backend.MultiServer;

namespace DriftBuster.Cli.Commands;

/// <summary>
/// <c>driftbuster multi-server</c>: reads a <see cref="MultiServerRequest"/> from stdin (strictly: unknown keys and wrong types are
/// refused), runs <see cref="MultiServerRunner"/> and writes one JSON object per line: <c>{"type": "progress", "progress": ...}</c> per
/// update, then <c>{"type": "result", "result": ...}</c> with exit code 0, or <c>{"type": "error", "message": ...}</c> with exit code 1.
/// </summary>
internal static class MultiServerCommand
{
    public static Command Build(Func<TextReader> stdin)
    {
        var command = new Command("multi-server", "Run a multi-server scan request read from stdin; newline-delimited JSON on stdout.");
        command.SetAction(parseResult => Execute(stdin(), parseResult.InvocationConfiguration.Output));
        return command;
    }

    internal static int Execute(TextReader stdin, TextWriter stdout)
    {
        try
        {
            var request = JsonSerializer.Deserialize(stdin.ReadToEnd(), CliJsonContext.Default.MultiServerRequest)
                ?? throw new JsonException("The request is null.");
            var version = request.SchemaVersion ?? MultiServerSchema.Version;
            if (!string.Equals(version, MultiServerSchema.Version, StringComparison.Ordinal))
            {
                return Emit(stdout, new MultiServerLine("error", Message: $"Unsupported schema version: {version}"), 1);
            }

            var cacheDir = request.CacheDir is { Length: > 0 } dir ? Directory.CreateDirectory(dir).FullName : DriftbusterPaths.GetCacheDirectory("diffs");
            var response = new MultiServerRunner(cacheDir).Run(request.Plans.Select(MultiServerPlan.FromServerScanPlan), new LineProgress(stdout));
            return Emit(stdout, new MultiServerLine("result", Result: response), 0);
        }
        catch (JsonException exc)
        {
            return Emit(stdout, new MultiServerLine("error", Message: $"Invalid request: {exc.Path ?? "$"}: {exc.Message}"), 1);
        }
        catch (Exception exc) when (exc is not OutOfMemoryException)
        {
            return Emit(stdout, new MultiServerLine("error", Message: $"Unhandled error: {exc.Message}"), 1);
        }
    }

    private static int Emit(TextWriter stdout, MultiServerLine line, int exitCode)
    {
        stdout.Write(JsonSerializer.Serialize(line, CliJsonContext.Default.MultiServerLine));
        stdout.Write('\n');
        stdout.Flush();
        return exitCode;
    }

    /// <summary>Writes every progress update as its line as soon as the runner reports it.</summary>
    private sealed class LineProgress(TextWriter stdout) : IProgress<ScanProgress>
    {
        public void Report(ScanProgress value) => Emit(stdout, new MultiServerLine("progress", Progress: value), 0);
    }
}
