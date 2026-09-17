using System.CommandLine;

using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Profiles.Run;

namespace DriftBuster.Cli.Commands;

/// <summary>
/// <c>driftbuster profile create|list|show|run</c>: the run profile commands of <c>python -m driftbuster.run_profiles_cli</c> (and
/// <c>python -m driftbuster.cli run-profile</c>) over <see cref="RunProfileCommands"/>. <c>--base-dir</c> is accepted before or after the
/// subcommand.
/// </summary>
internal static class ProfileCommand
{
    /// <summary><c>--base-dir</c>, shared by the profile and schedule commands.</summary>
    public static Option<string?> BaseDirOption()
        => new("--base-dir") { Description = "Override the profiles root directory (defaults to ./Profiles).", Recursive = true };

    public static Command Build()
    {
        var baseDir = BaseDirOption();
        var command = new Command("profile", "Manage DriftBuster run profiles.") { baseDir };
        command.Subcommands.Add(BuildCreate(baseDir));
        command.Subcommands.Add(BuildList(baseDir));
        command.Subcommands.Add(BuildShow(baseDir));
        command.Subcommands.Add(BuildRun(baseDir));
        return command;
    }

    internal static string? BaseDir(ParseResult parseResult, Option<string?> option)
        => parseResult.GetValue(option) is { } text ? PythonPurePath.Str(text) : null;

    private static Command BuildCreate(Option<string?> baseDir)
    {
        var name = new Option<string>("--name") { Required = true };
        var description = PythonArguments.OptionalText("--description", "Profile description.");
        var source = PythonArguments.Append("--source", "File, directory, or glob to include (repeatable).");
        var baseline = PythonArguments.OptionalText("--baseline", "Source path that should act as the baseline (defaults to first source).");
        var option = PythonArguments.Append("--option", "Custom option in key=value format (repeatable).");
        var ignoreRule = PythonArguments.Append("--secret-ignore-rule", "Secret scanner rule names to ignore (repeatable).");
        var ignorePattern = PythonArguments.Append("--secret-ignore-pattern", "Regular expressions to suppress secret findings (repeatable).");
        var command = new Command("create", "Create or update a run profile.") { name, description, source, baseline, option, ignoreRule, ignorePattern };
        command.SetAction(parseResult => CommandRunner.Run(parseResult, (stdout, _) =>
        {
            var profile = RunProfileCommands.Create(
                parseResult.GetValue(name)!,
                parseResult.GetValue(description),
                parseResult.GetValue(source),
                parseResult.GetValue(baseline),
                parseResult.GetValue(option),
                parseResult.GetValue(ignoreRule),
                parseResult.GetValue(ignorePattern),
                BaseDir(parseResult, baseDir));
            ConsoleText.Print(stdout, $"Saved profile '{profile.Name}'");
            return 0;
        }));
        return command;
    }

    private static Command BuildList(Option<string?> baseDir)
    {
        var command = new Command("list", "List available profiles.");
        command.SetAction(parseResult => CommandRunner.Run(parseResult, (stdout, _) =>
        {
            foreach (var line in RunProfileCommands.ListProfileLines(BaseDir(parseResult, baseDir)))
            {
                ConsoleText.Print(stdout, line);
            }

            return 0;
        }));
        return command;
    }

    private static Command BuildShow(Option<string?> baseDir)
    {
        var name = PythonArguments.Positional("name", "Profile name.");
        var command = new Command("show", "Show profile configuration.") { name };
        command.SetAction(parseResult => CommandRunner.Run(parseResult, (stdout, _) =>
        {
            var payload = RunProfileCommands.Show(parseResult.GetValue(name)!, BaseDir(parseResult, baseDir));
            ConsoleText.Print(stdout, ConsoleText.Dumps(payload, indent: 2, sortKeys: true));
            return 0;
        }));
        return command;
    }

    private static Command BuildRun(Option<string?> baseDir)
    {
        var name = PythonArguments.OptionalText("--name", "Name of a saved profile.");
        var profile = PythonArguments.OptionalText("--profile", "Path to a profile JSON file.");
        var timestamp = PythonArguments.OptionalText("--timestamp", "Override run timestamp (UTC).");
        var save = PythonArguments.Flag("--save", "Persist the supplied profile before running.");
        var ignoreRule = PythonArguments.Append("--secret-ignore-rule", "Secret scanner rule names to ignore for this run (repeatable).");
        var ignorePattern = PythonArguments.Append("--secret-ignore-pattern", "Regular expressions to suppress secret findings for this run (repeatable).");
        var command = new Command("run", "Execute a profile run.") { name, profile, timestamp, save, ignoreRule, ignorePattern };
        // add_mutually_exclusive_group(required=True): exactly one of --name and --profile.
        command.Validators.Add(result =>
        {
            var given = new[] { result.GetResult(name), result.GetResult(profile) }.Count(option => option is { Implicit: false });
            if (given != 1)
            {
                result.AddError(given == 0
                    ? "one of the arguments --name --profile is required"
                    : "argument --profile: not allowed with argument --name");
            }
        });
        command.SetAction(parseResult => CommandRunner.Run(parseResult, (stdout, _) =>
        {
            var result = RunProfileCommands.Run(
                parseResult.GetValue(profile),
                parseResult.GetValue(name),
                BaseDir(parseResult, baseDir),
                parseResult.GetValue(save),
                parseResult.GetValue(timestamp),
                parseResult.GetValue(ignoreRule),
                parseResult.GetValue(ignorePattern));
            ConsoleText.Print(stdout, $"Run saved to {result.OutputDir}");
            ConsoleText.Print(stdout, $"Files collected: {result.Files.Count}");
            return 0;
        }));
        return command;
    }
}
