using System.CommandLine;

using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Scheduling;

namespace DriftBuster.Cli.Commands;

/// <summary>
/// <c>driftbuster schedule list|due|mark-complete|skip-until</c> over <see cref="ScheduleCommands"/>, each payload printed as <c>json.dumps(payload, indent=2, sort_keys=True)</c> and a new line.
/// </summary>
internal static class ScheduleCommand
{
    public static Command Build()
    {
        var baseDir = ProfileCommand.BaseDirOption();
        var command = new Command("schedule", "Inspect and manage run profile schedules.") { baseDir };
        command.Subcommands.Add(BuildList(baseDir));
        command.Subcommands.Add(BuildDue(baseDir));
        command.Subcommands.Add(BuildMarkComplete(baseDir));
        command.Subcommands.Add(BuildSkipUntil(baseDir));
        return command;
    }

    private static (Option<string?> Config, Option<string?> State) Common(Command command)
    {
        var config = EngineArguments.OptionalText("--config", "Path to the schedules manifest (defaults to Profiles/schedules.json).");
        var state = EngineArguments.OptionalText("--state", "Path to persist scheduler state (defaults to Profiles/scheduler-state.json).");
        command.Options.Add(config);
        command.Options.Add(state);
        return (config, state);
    }

    private static string? PathValue(ParseResult parseResult, Option<string?> option)
        => parseResult.GetValue(option) is { } text ? LexicalPath.Str(text) : null;

    private static void PrintJson(TextWriter stdout, object? payload)
    {
        var text = ConsoleText.Dumps(payload, indent: 2, sortKeys: true);
        ConsoleText.Write(stdout, text.EndsWith('\n') ? text : text + "\n");
    }

    private static Command BuildList(Option<string?> baseDir)
    {
        var command = new Command("list", "List registered schedules and their next run windows.");
        var (config, state) = Common(command);
        command.SetAction(parseResult => CommandRunner.Run(parseResult, (stdout, _) =>
        {
            PrintJson(stdout, ScheduleCommands.List(ProfileCommand.BaseDir(parseResult, baseDir), PathValue(parseResult, config), PathValue(parseResult, state)));
            return 0;
        }));
        return command;
    }

    private static Command BuildDue(Option<string?> baseDir)
    {
        var command = new Command("due", "Return runs that are due as of the supplied timestamp.");
        var (config, state) = Common(command);
        var at = EngineArguments.OptionalText("--at", "Reference timestamp in ISO 8601 format (defaults to current UTC time).");
        command.Options.Add(at);
        command.SetAction(parseResult => CommandRunner.Run(parseResult, (stdout, _) =>
        {
            PrintJson(stdout, ScheduleCommands.Due(
                parseResult.GetValue(at), ProfileCommand.BaseDir(parseResult, baseDir), PathValue(parseResult, config), PathValue(parseResult, state)));
            return 0;
        }));
        return command;
    }

    private static Command BuildMarkComplete(Option<string?> baseDir)
    {
        var command = new Command("mark-complete", "Mark a pending run complete and advance its schedule.");
        var (config, state) = Common(command);
        var name = new Option<string>("--name") { Required = true, Description = "Schedule name to mark complete." };
        var completedAt = EngineArguments.OptionalText("--completed-at", "Completion timestamp in ISO 8601 format (defaults to the pending time).");
        command.Options.Add(name);
        command.Options.Add(completedAt);
        command.SetAction(parseResult => CommandRunner.Run(parseResult, (stdout, _) =>
        {
            PrintJson(stdout, ScheduleCommands.MarkComplete(
                parseResult.GetValue(name)!,
                parseResult.GetValue(completedAt),
                ProfileCommand.BaseDir(parseResult, baseDir),
                PathValue(parseResult, config),
                PathValue(parseResult, state)));
            return 0;
        }));
        return command;
    }

    private static Command BuildSkipUntil(Option<string?> baseDir)
    {
        var command = new Command("skip-until", "Skip the schedule until the supplied resume timestamp.");
        var (config, state) = Common(command);
        var name = new Option<string>("--name") { Required = true, Description = "Schedule name to update." };
        var resumeAt = new Option<string>("--resume-at") { Required = true, Description = "Resume timestamp in ISO 8601 format." };
        command.Options.Add(name);
        command.Options.Add(resumeAt);
        command.SetAction(parseResult => CommandRunner.Run(parseResult, (stdout, _) =>
        {
            PrintJson(stdout, ScheduleCommands.SkipUntil(
                parseResult.GetValue(name)!,
                parseResult.GetValue(resumeAt)!,
                ProfileCommand.BaseDir(parseResult, baseDir),
                PathValue(parseResult, config),
                PathValue(parseResult, state)));
            return 0;
        }));
        return command;
    }
}
