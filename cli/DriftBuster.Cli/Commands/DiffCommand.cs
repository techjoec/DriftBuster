using System.CommandLine;
using System.Numerics;
using System.Text;

using DriftBuster.Backend.Diff;
using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Profiles.Run;
using DriftBuster.Backend.Reporting;

namespace DriftBuster.Cli.Commands;

/// <summary>
/// <c>driftbuster diff BASELINE COMPARISON...</c>: each comparison is diffed against the baseline
/// with <see cref="DiffBuilder.BuildUnifiedDiff"/> and printed, or written to <c>{baseline stem}--{comparison stem}.patch</c> under
/// <c>--output-dir</c>, followed by its summary line. The <c>auto</c> content type comes from detection, not the file extension.
/// </summary>
internal static class DiffCommand
{
    private const string Prog = "driftbuster diff";

    // path.read_text(encoding="utf-8", errors="ignore"): invalid sequences dropped.
    private static readonly Encoding Utf8Ignore = Encoding.GetEncoding("utf-8", EncoderFallback.ExceptionFallback, new DecoderReplacementFallback(string.Empty));

    /// <summary>The parsed arguments of <c>_build_diff_parser</c>.</summary>
    internal sealed record Arguments(
        string Baseline,
        IReadOnlyList<string> Comparisons,
        string ContentType,
        BigInteger ContextLines,
        IReadOnlyList<string> MaskTokens,
        string Placeholder,
        string? OutputDir);

    public static Command Build()
    {
        var baseline = EngineArguments.Positional("baseline", "Baseline file to diff against.");
        var comparisons = new Argument<string[]>("comparisons") { Arity = ArgumentArity.OneOrMore, Description = "One or more files to compare with the baseline." };
        var contentType = EngineArguments.Text("--content-type", "auto", "Canonicalisation strategy applied before diffing (default: auto).");
        contentType.AcceptOnlyFromAmong("auto", "text", "xml");
        var contextLines = EngineArguments.Int("--context-lines", 3, "Context lines to include around each diff hunk (default: 3).");
        var maskTokens = EngineArguments.Append("--mask-token", "Token to redact before diffing (repeatable).");
        var placeholder = EngineArguments.Text("--placeholder", RedactionFilter.DefaultPlaceholder, "Placeholder shown for masked tokens (default: [REDACTED]).");
        var outputDir = EngineArguments.OptionalText("--output-dir", "Directory where unified diff patches will be written.");
        var command = new Command("diff", "Generate unified diffs for configuration snapshots.")
        {
            baseline, comparisons, contentType, contextLines, maskTokens, placeholder, outputDir,
        };
        command.SetAction(parseResult => CommandRunner.Run(parseResult, (stdout, stderr) => Execute(
            new Arguments(
                parseResult.GetValue(baseline)!,
                parseResult.GetValue(comparisons)!,
                parseResult.GetValue(contentType)!,
                parseResult.GetValue(contextLines),
                parseResult.GetValue(maskTokens)!,
                parseResult.GetValue(placeholder)!,
                parseResult.GetValue(outputDir)),
            stdout,
            stderr)));
        return command;
    }

    /// <summary><c>_run_diff(argv)</c> after parsing.</summary>
    internal static int Execute(Arguments args, TextWriter stdout, TextWriter stderr)
    {
        if (args.ContextLines < 0)
        {
            return CommandRunner.ParserError(stderr, Prog, "--context-lines must be zero or greater");
        }

        var baselinePath = EnsureFile(args.Baseline, "Baseline path", out var refusal);
        var comparisonPaths = new List<string>();
        foreach (var comparison in refusal is null ? args.Comparisons : [])
        {
            comparisonPaths.Add(EnsureFile(comparison, "Comparison path", out refusal));
            if (refusal is not null)
            {
                break;
            }
        }

        if (refusal is not null)
        {
            return CommandRunner.ParserError(stderr, Prog, refusal);
        }

        string baselineContent;
        try
        {
            baselineContent = ReadText(baselinePath);
        }
        catch (FileNotFoundException exc)
        {
            return CommandRunner.ParserError(stderr, Prog, exc.Message);
        }

        string? outputDir = null;
        if (args.OutputDir is not null)
        {
            outputDir = EnginePath.Resolve(EnginePath.ExpandUser(args.OutputDir));
            EnginePath.MakeDirectories(outputDir);
        }

        var exitCode = 0;
        foreach (var candidatePath in comparisonPaths)
        {
            exitCode = Math.Max(exitCode, Compare(args, baselinePath, baselineContent, candidatePath, outputDir, stdout, stderr));
        }

        return exitCode;
    }

    private static int Compare(Arguments args, string baselinePath, string baselineContent, string candidatePath, string? outputDir, TextWriter stdout, TextWriter stderr)
    {
        string candidateContent;
        try
        {
            candidateContent = ReadText(candidatePath);
        }
        catch (FileNotFoundException exc)
        {
            ConsoleText.Write(stderr, $"error: {exc.Message}\n");
            return 1;
        }

        var contentType = string.Equals(args.ContentType, "auto", StringComparison.Ordinal)
            ? ContentTypeResolver.ResolvePair(baselinePath, candidatePath)
            : args.ContentType;
        var tokens = args.MaskTokens.Where(token => token.Length > 0).ToList();
        var result = DiffBuilder.BuildUnifiedDiff(
            baselineContent,
            candidateContent,
            contentType,
            fromLabel: PathText.Name(baselinePath),
            toLabel: PathText.Name(candidatePath),
            maskTokens: tokens.Count > 0 ? tokens : null,
            placeholder: args.Placeholder,
            contextLines: (int)BigInteger.Min(args.ContextLines, int.MaxValue));
        if (outputDir is not null)
        {
            var destination = LexicalPath.Join(outputDir, PatchName(baselinePath, candidatePath));
            var text = result.Diff.EndsWith('\n') ? result.Diff : result.Diff + "\n";
            EngineTextFile.WriteText(destination, ReportValues.TextModeNewLines(text));
            ConsoleText.Write(stdout, $"Wrote diff for {PathText.Name(candidatePath)} to {destination}\n");
        }
        else
        {
            ConsoleText.Write(stdout, $"=== {PathText.Name(baselinePath)} → {PathText.Name(candidatePath)} ({contentType}) ===\n");
            ConsoleText.Write(stdout, result.Diff.Trim('\n').Length > 0 ? $"{result.Diff}\n" : "(no differences)\n");
        }

        ConsoleText.Write(stdout, $"Summary: added={result.Stats.AddedLines} removed={result.Stats.RemovedLines} changed={result.Stats.ChangedLines}\n");
        return 0;
    }

    // _ensure_file: path.expanduser().resolve(), which must exist and be a file.
    private static string EnsureFile(string path, string role, out string? refusal)
    {
        var resolved = EnginePath.Resolve(EnginePath.ExpandUser(path));
        refusal = !RunProfileStore.Exists(resolved)
            ? $"{role} does not exist: {resolved}"
            : !EnginePath.IsFile(resolved) ? $"{role} must be a file: {resolved}" : null;
        return resolved;
    }

    /// <summary>
    /// <c>_read_text(path)</c>: the file decoded as UTF-8 with invalid bytes dropped and universal newlines translated; an <c>OSError</c>
    /// becomes <c>FileNotFoundError("Unable to read {path}: {exc}")</c>.
    /// </summary>
    internal static string ReadText(string path)
    {
        byte[] raw;
        try
        {
            raw = EngineTextFile.ReadBytes(path, LexicalPath.Str(path));
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
        {
            throw new FileNotFoundException($"Unable to read {LexicalPath.Str(path)}: {exc.Message}", path, exc);
        }

        return Utf8Ignore.GetString(raw).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
    }

    /// <summary><c>_build_patch_name(baseline, candidate)</c>: <c>{left}--{right}.patch</c> from the stems with spaces as hyphens.</summary>
    internal static string PatchName(string baseline, string candidate)
    {
        var left = Stem(baseline).Replace(' ', '-');
        var right = Stem(candidate).Replace(' ', '-');
        return $"{(left.Length > 0 ? left : "baseline")}--{(right.Length > 0 ? right : "comparison")}.patch";
    }

    private static string Stem(string path)
    {
        var name = PathText.Name(path);
        return name[..(name.Length - PathText.Suffix(path).Length)];
    }
}
