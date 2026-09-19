using System.CommandLine;

using DriftBuster.Backend.Json;
using DriftBuster.Backend.Profiles.Detection;

namespace DriftBuster.Cli.Commands;

/// <summary>
/// <c>driftbuster detection-profile summary|diff|hunt-bridge</c> over <see cref="DetectionProfileCommands"/>: each result as indented
/// JSON (<see cref="ModelJson"/>) on stdout, or into <c>--output</c>.
/// </summary>
internal static class DetectionProfileCommand
{
    public static Command Build()
    {
        var command = new Command("detection-profile", "Summarise, diff and bridge detection profile stores.");
        command.Subcommands.Add(BuildSummary());
        command.Subcommands.Add(BuildDiff());
        command.Subcommands.Add(BuildHuntBridge());
        return command;
    }

    private static Option<string?> OutputOption(Command command)
    {
        var output = new Option<string?>("--output") { Description = "File to write the JSON to (defaults to stdout)." };
        command.Options.Add(output);
        return output;
    }

    private static int Write<T>(T result, string? output, TextWriter stdout)
    {
        var text = ModelJson.Serialize(result);
        if (output is null)
        {
            ConsoleText.Write(stdout, text);
        }
        else
        {
            File.WriteAllText(output, text);
        }

        return 0;
    }

    private static Command BuildSummary()
    {
        var store = new Argument<string>("store") { Description = "Detection profile store JSON file." };
        var command = new Command("summary", "Summarise a detection profile store.") { store };
        var output = OutputOption(command);
        command.SetAction(parseResult => CommandRunner.Run(parseResult, (stdout, _) =>
            Write(DetectionProfileCommands.Summary(parseResult.GetValue(store)!), parseResult.GetValue(output), stdout)));
        return command;
    }

    private static Command BuildDiff()
    {
        var baseline = new Argument<string>("baseline") { Description = "Baseline summary JSON file." };
        var current = new Argument<string>("current") { Description = "Current summary JSON file." };
        var command = new Command("diff", "Compare two detection profile summaries.") { baseline, current };
        var output = OutputOption(command);
        command.SetAction(parseResult => CommandRunner.Run(parseResult, (stdout, _) =>
            Write(DetectionProfileCommands.Diff(parseResult.GetValue(baseline)!, parseResult.GetValue(current)!), parseResult.GetValue(output), stdout)));
        return command;
    }

    private static Command BuildHuntBridge()
    {
        var store = new Argument<string>("store") { Description = "Detection profile store JSON file." };
        var hunt = new Argument<string>("hunt") { Description = "JSON array written by driftbuster hunt." };
        var tags = new Option<string[]>("--tag") { Description = "Scan tag used when matching profile configs (repeatable).", AllowMultipleArgumentsPerToken = false };
        var root = new Option<string?>("--root") { Description = "Scanned root, to turn hit paths into profile-relative paths." };
        var command = new Command("hunt-bridge", "Attach matching profile configs to hunt hits.") { store, hunt, tags, root };
        var output = OutputOption(command);
        command.SetAction(parseResult => CommandRunner.Run(parseResult, (stdout, _) => Write(
            DetectionProfileCommands.HuntBridge(parseResult.GetValue(store)!, parseResult.GetValue(hunt)!, parseResult.GetValue(tags), parseResult.GetValue(root)),
            parseResult.GetValue(output),
            stdout)));
        return command;
    }
}
