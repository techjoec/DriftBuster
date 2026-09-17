using System.CommandLine;
using System.Numerics;

using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Profiles.Detection;
using DriftBuster.Backend.Reporting;

namespace DriftBuster.Cli.Commands;

/// <summary>
/// <c>driftbuster detection-profile summary|diff|hunt-bridge</c>: <c>python -m driftbuster.profile_cli</c> over
/// <see cref="DetectionProfileCommands"/>. Every command writes its payload with <c>_write_json</c>; an exception from the command is
/// written as <c>error: {exc}</c> on stderr with exit code 1.
/// </summary>
internal static class DetectionProfileCommand
{
    /// <summary>The <c>--indent</c>, <c>--sort-keys</c> and <c>--output</c> values of <c>_add_output_options</c>.</summary>
    internal sealed record OutputOptions(BigInteger Indent, bool SortKeys, string? Output);

    public static Command Build()
    {
        var command = new Command("detection-profile", "Profile summary and diff helper for manual audits.");
        command.Subcommands.Add(BuildSummary());
        command.Subcommands.Add(BuildDiff());
        command.Subcommands.Add(BuildHuntBridge());
        return command;
    }

    private static (Option<BigInteger> Indent, Option<bool> SortKeys, Option<string?> Output) OutputOptionsFor(Command command)
    {
        var indent = PythonArguments.Int("--indent", 2, "JSON indentation level (0 for compact output).");
        var sortKeys = PythonArguments.Flag("--sort-keys", "Sort keys before writing JSON output.");
        var output = PythonArguments.OptionalText("--output", "Optional file to write results to (defaults to stdout).");
        command.Options.Add(indent);
        command.Options.Add(sortKeys);
        command.Options.Add(output);
        return (indent, sortKeys, output);
    }

    private static OutputOptions Read(ParseResult parseResult, (Option<BigInteger> Indent, Option<bool> SortKeys, Option<string?> Output) options)
        => new(parseResult.GetValue(options.Indent), parseResult.GetValue(options.SortKeys), parseResult.GetValue(options.Output));

    private static Command BuildSummary()
    {
        var store = PythonArguments.Positional("store", "Path to JSON payload compatible with ProfileStore.from_dict().");
        var command = new Command("summary", "Generate a profile summary from a ProfileStore payload.") { store };
        var options = OutputOptionsFor(command);
        command.SetAction(parseResult => CommandRunner.Run(parseResult, (stdout, stderr) => Handle(
            () => DetectionProfileCommands.Summary(PythonPurePath.Str(parseResult.GetValue(store)!)), Read(parseResult, options), stdout, stderr)));
        return command;
    }

    private static Command BuildDiff()
    {
        var baseline = PythonArguments.Positional("baseline", "Baseline summary JSON file.");
        var current = PythonArguments.Positional("current", "Current summary JSON file.");
        var command = new Command("diff", "Diff two stored profile summary JSON payloads.") { baseline, current };
        var options = OutputOptionsFor(command);
        command.SetAction(parseResult => CommandRunner.Run(parseResult, (stdout, stderr) => Handle(
            () => DetectionProfileCommands.Diff(PythonPurePath.Str(parseResult.GetValue(baseline)!), PythonPurePath.Str(parseResult.GetValue(current)!)),
            Read(parseResult, options),
            stdout,
            stderr)));
        return command;
    }

    private static Command BuildHuntBridge()
    {
        var store = PythonArguments.Positional("store", "ProfileStore JSON payload (same format as the summary command).");
        var hunt = PythonArguments.Positional("hunt", "JSON array produced by driftbuster hunt (hunt_path(..., return_json=True)).");
        var tags = PythonArguments.Append("--tag", "Activation tag applied when matching profile configs (repeatable).");
        var root = PythonArguments.OptionalText("--root", "Base path used to resolve hunt absolute paths into profile-relative paths.");
        var command = new Command("hunt-bridge", "Attach profile metadata to hunt hits for manual review.") { store, hunt, tags, root };
        var options = OutputOptionsFor(command);
        command.SetAction(parseResult => CommandRunner.Run(parseResult, (stdout, stderr) => Handle(
            () => DetectionProfileCommands.HuntBridge(
                PythonPurePath.Str(parseResult.GetValue(store)!),
                PythonPurePath.Str(parseResult.GetValue(hunt)!),
                parseResult.GetValue(tags)!,
                parseResult.GetValue(root) is { } rootText ? PythonPurePath.Str(rootText) : null),
            Read(parseResult, options),
            stdout,
            stderr)));
        return command;
    }

    /// <summary><c>main</c>'s handler call: the payload written with <see cref="WriteJson"/>, or <c>error: {exc}</c> and exit code 1.</summary>
    internal static int Handle(Func<object?> produce, OutputOptions options, TextWriter stdout, TextWriter stderr)
    {
        try
        {
            WriteJson(produce(), options, stdout);
            return 0;
        }
        catch (Exception exc) when (exc is not OutOfMemoryException)
        {
            ConsoleText.Write(stderr, $"error: {exc.Message}\n");
            return 1;
        }
    }

    /// <summary>
    /// <c>_write_json(payload, output, indent, sort_keys)</c>: <c>json.dumps</c> with <c>indent=None</c> when <c>indent &lt;= 0</c>, a new line
    /// appended unless the text ends with one, written to stdout or to <c>output</c>.
    /// </summary>
    internal static void WriteJson(object? payload, OutputOptions options, TextWriter stdout)
    {
        int? indent = options.Indent <= 0 ? null : (int)BigInteger.Min(options.Indent, int.MaxValue);
        var text = ConsoleText.Dumps(payload, indent, options.SortKeys);
        text += text.EndsWith('\n') ? string.Empty : "\n";
        if (options.Output is null)
        {
            ConsoleText.Write(stdout, text);
        }
        else
        {
            PythonTextFile.WriteText(PythonPurePath.Str(options.Output), ReportValues.TextModeNewLines(text));
        }
    }
}
