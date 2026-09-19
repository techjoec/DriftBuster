using System.CommandLine;
using System.Globalization;
using System.Numerics;

using DriftBuster.Backend.Detection;
using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Json;
using DriftBuster.Backend.Profiles.Run;

namespace DriftBuster.Cli.Commands;

/// <summary>
/// <c>driftbuster scan PATH</c>: detects a file or every file under a directory and prints a table, or one JSON object per file
/// (<c>--json</c>).
/// </summary>
internal static class ScanCommand
{
    private const string Prog = "driftbuster";
    private const string Missing = "—";
    private const int MaxHintWidth = 72;
    private const int MaxMetadataWidth = 48;

    private static readonly string[] Columns = ["Path", "Format", "Variant", "Confidence", "Severity", "Severity hint", "Metadata keys"];

    public static Command Build()
    {
        var path = EngineArguments.Positional("path", "File or directory to scan.");
        var glob = EngineArguments.Text("--glob", "**/*", "Glob used when scanning directories (default: **/*).");
        var sampleSize = EngineArguments.OptionalInt("--sample-size", "Bytes to sample from each file (defaults to detector setting).");
        var json = EngineArguments.Flag("--json", "Emit JSON lines instead of a table.");
        var command = new Command("scan", "Scan files for DriftBuster formats.") { path, glob, sampleSize, json };
        command.SetAction(parseResult => CommandRunner.Run(parseResult, (stdout, stderr) => Execute(
            parseResult.GetValue(path)!,
            parseResult.GetValue(glob)!,
            parseResult.GetValue(sampleSize),
            parseResult.GetValue(json),
            stdout,
            stderr)));
        return command;
    }

    /// <summary>Exit code 2 with <c>Path does not exist: {path}</c> for a missing path.</summary>
    internal static int Execute(string path, string glob, BigInteger? sampleSize, bool json, TextWriter stdout, TextWriter stderr)
    {
        var root = LexicalPath.Str(path);
        IReadOnlyList<(string Path, DetectionMatch? Match)> results;
        // A tree scan reports an unreadable file and carries on; a single file, or a root that cannot be listed, still fails.
        var detector = new SkippingDetector(ConsoleText.DetectorSampleSize(sampleSize, Warn), Detector.DefaultTotalSampleBudget, Warn)
        {
            OnSkipped = (_, error) => Warn($"Skipped unreadable file: {error.Message}"),
        };
        if (EnginePath.IsFile(root))
        {
            results = [(root, detector.ScanFile(root))];
        }
        else if (!RunProfileStore.Exists(root))
        {
            return CommandRunner.ParserError(stderr, Prog, $"Path does not exist: {root}");
        }
        else
        {
            results = detector.ScanPath(root, glob);
        }

        if (json)
        {
            EmitJson(root, results, stdout);
        }
        else
        {
            EmitTable(root, results, stdout);
        }

        return 0;

        void Warn(string message) => ConsoleText.Print(stderr, message);
    }

    /// <summary>
    /// The path relative to the root with forward slashes, or the whole path with forward slashes outside it. The walk reports absolute
    /// paths, so a relative root is also tried in its absolute form.
    /// </summary>
    internal static string RelativePath(string root, string path)
        => LexicalPath.RelativeTo(path, root)
            ?? (LexicalPath.IsAbsolute(path) && !LexicalPath.IsAbsolute(root) ? LexicalPath.RelativeTo(path, EnginePath.Absolute(root)) : null)
            ?? PathText.ToPosix(LexicalPath.Str(path));

    /// <summary>Truncated with an ellipsis to <paramref name="limit"/> code points.</summary>
    internal static string Ellipsize(string value, int limit)
    {
        if (ConsoleText.Len(value) <= limit)
        {
            return value;
        }

        return limit <= 1 ? ConsoleText.Head(value, Math.Max(limit, 0)) : ConsoleText.Head(value, limit - 1) + "…";
    }

    private static void EmitTable(string root, IReadOnlyList<(string Path, DetectionMatch? Match)> results, TextWriter stdout)
    {
        var widths = Columns.Select(column => Math.Max(ConsoleText.Len(column), 4)).ToArray();
        var rows = new List<string[]>();
        foreach (var (path, match) in results)
        {
            var row = TableRow(root, path, match);
            rows.Add(row);
            for (var index = 0; index < row.Length; index++)
            {
                widths[index] = Math.Max(widths[index], ConsoleText.Len(row[index]));
            }
        }

        widths[5] = Math.Min(widths[5], MaxHintWidth);
        widths[6] = Math.Min(widths[6], MaxMetadataWidth);
        ConsoleText.Print(stdout, string.Join("  ", Columns.Select((column, index) => ConsoleText.LeftJustify(column, widths[index]))));
        ConsoleText.Print(stdout, string.Join("  ", widths.Select(width => new string('-', width))));
        foreach (var row in rows)
        {
            ConsoleText.Print(stdout, string.Join("  ", row.Select((value, index) => ConsoleText.LeftJustify(Ellipsize(value, widths[index]), widths[index]))));
        }
    }

    private static string[] TableRow(string root, string path, DetectionMatch? match)
    {
        var severity = Missing;
        var severityHint = Missing;
        var metadataKeys = Missing;
        if (match?.Metadata is { Count: > 0 } metadata)
        {
            metadataKeys = string.Join(", ", metadata.Select(pair => pair.Key).Order(StringComparer.Ordinal));
            severity = metadata.Text("catalog_severity") is { Length: > 0 } text ? text : Missing;
            severityHint = metadata.Text("catalog_severity_hint") is { Length: > 0 } hint ? hint : Missing;
        }

        return
        [
            RelativePath(root, path),
            match?.FormatName ?? Missing,
            string.IsNullOrEmpty(match?.Variant) ? Missing : match.Variant,
            match is null ? Missing : match.Confidence.ToString("0.00", CultureInfo.InvariantCulture),
            severity,
            severityHint,
            metadataKeys,
        ];
    }

    private static void EmitJson(string root, IReadOnlyList<(string Path, DetectionMatch? Match)> results, TextWriter stdout)
    {
        foreach (var (path, match) in results)
        {
            var line = match is null
                ? new ScanLine(RelativePath(root, path), Detected: false)
                : new ScanLine(
                    RelativePath(root, path),
                    Detected: true,
                    match.FormatName,
                    match.Variant,
                    match.Confidence,
                    match.Metadata.Text("catalog_severity"),
                    match.Metadata.Text("catalog_severity_hint"),
                    match.Metadata);
            ConsoleText.Print(stdout, CliJson.Line(line));
        }
    }
}
