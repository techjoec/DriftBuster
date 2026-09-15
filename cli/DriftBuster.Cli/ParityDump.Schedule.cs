using System.CommandLine;

using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Scheduling;

namespace DriftBuster.Cli;

/// <summary>The <c>schedule</c> surface of <c>parity-dump</c> (py_dump.py <c>cmd_schedule</c>).</summary>
public static partial class ParityDump
{
    private static readonly string[] ScheduleCommandChoices = ["list", "due", "mark-complete", "skip-until"];

    /// <summary>The optional arguments of one schedule command, each forwarded only when given.</summary>
    internal sealed record ScheduleArguments(string? At, string? Name, string? CompletedAt, string? ResumeAt, string Now);

    private static Command BuildSchedule()
    {
        var configArgument = new Argument<string>("config") { Description = "Schedules manifest." };
        var stateArgument = new Argument<string>("state") { Description = "Scheduler state file (absent means no state)." };
        var commandArgument = new Argument<string>("command") { Description = "list, due, mark-complete or skip-until." };
        commandArgument.AcceptOnlyFromAmong(ScheduleCommandChoices);
        var atOption = new Option<string?>("--at") { Description = "due: reference timestamp." };
        var nameOption = new Option<string?>("--name") { Description = "mark-complete, skip-until: schedule name." };
        var completedAtOption = new Option<string?>("--completed-at") { Description = "mark-complete: completion timestamp." };
        var resumeAtOption = new Option<string?>("--resume-at") { Description = "skip-until: resume timestamp." };
        var nowOption = new Option<string>("--now") { DefaultValueFactory = _ => "2025-03-01T12:00:00+00:00", Description = "ProfileScheduler._now." };
        var command = new Command("schedule", "A run_profiles_cli schedule command's payload and the state file it leaves.");
        command.Arguments.Add(configArgument);
        command.Arguments.Add(stateArgument);
        command.Arguments.Add(commandArgument);
        foreach (var option in new Option[] { atOption, nameOption, completedAtOption, resumeAtOption, nowOption })
        {
            command.Options.Add(option);
        }

        command.SetAction(parseResult =>
        {
            var arguments = new ScheduleArguments(
                parseResult.GetValue(atOption),
                parseResult.GetValue(nameOption),
                parseResult.GetValue(completedAtOption),
                parseResult.GetValue(resumeAtOption),
                parseResult.GetValue(nowOption)!);
            WriteLines(parseResult, [Schedule(parseResult.GetValue(configArgument)!, parseResult.GetValue(stateArgument)!, parseResult.GetValue(commandArgument)!, arguments)]);
            return 0;
        });
        return command;
    }

    /// <summary>
    /// Runs one <see cref="ScheduleCommands"/> command over the manifest and a scratch copy of the state file with
    /// <see cref="ProfileScheduler.Now"/> fixed at <see cref="ScheduleArguments.Now"/>, and prints <c>output</c> (the payload the command
    /// prints) or <c>error</c>, then <c>state</c> (the scratch state file's text after the command, null when there is none).
    /// </summary>
    internal static string Schedule(string configPath, string statePath, string command, ScheduleArguments arguments)
    {
        var now = PythonDateTime.FromIsoFormat(arguments.Now);
        var scratch = Directory.CreateTempSubdirectory("driftbuster-parity-schedule-");
        var state = Path.Combine(scratch.FullName, "state.json");
        if (PythonPath.IsFile(statePath))
        {
            File.Copy(statePath, state);
        }

        var originalNow = ProfileScheduler.Now;
        ProfileScheduler.Now = () => now;
        try
        {
            var record = new OrderedDictionary<string, object?>(StringComparer.Ordinal);
            try
            {
                record["output"] = RunScheduleCommand(configPath, state, command, arguments);
            }
            catch (Exception exc) when (exc is not OutOfMemoryException)
            {
                record["error"] = ErrorPayload(exc);
            }

            // Path.read_text(encoding="utf-8"): strict UTF-8 over the bytes, a byte order mark kept as U+FEFF.
            record["state"] = File.Exists(state) ? UniversalNewlines(PythonUtf8.Decode(File.ReadAllBytes(state))) : null;
            return Keyed(record);
        }
        finally
        {
            ProfileScheduler.Now = originalNow;
            scratch.Delete(recursive: true);
        }
    }

    // Path.read_text(): universal newlines mode reads "\r\n" and "\r" as "\n".
    private static string UniversalNewlines(string text)
        => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    private static object RunScheduleCommand(string configPath, string state, string command, ScheduleArguments arguments)
    {
        if (!ArgumentsParse(command, arguments))
        {
            // argparse: a missing required option or an option the subcommand does not take ends main() with SystemExit(2).
            throw new CommandExitException("2");
        }

        return command switch
        {
            "list" => ScheduleCommands.List(null, configPath, state),
            "due" => ScheduleCommands.Due(arguments.At, null, configPath, state),
            "mark-complete" => ScheduleCommands.MarkComplete(arguments.Name!, arguments.CompletedAt, null, configPath, state),
            _ => ScheduleCommands.SkipUntil(arguments.Name!, arguments.ResumeAt!, null, configPath, state),
        };
    }

    // The options run_profiles_cli's schedule subparsers accept: due --at; mark-complete --name (required) and --completed-at; skip-until
    // --name and --resume-at (both required); list none.
    internal static bool ArgumentsParse(string command, ScheduleArguments arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return command switch
        {
            "list" => arguments is { At: null, Name: null, CompletedAt: null, ResumeAt: null },
            "due" => arguments is { Name: null, CompletedAt: null, ResumeAt: null },
            "mark-complete" => arguments is { Name: not null, At: null, ResumeAt: null },
            _ => arguments is { Name: not null, ResumeAt: not null, At: null, CompletedAt: null },
        };
    }
}
