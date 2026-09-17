using System.CommandLine;
using System.Numerics;

using DriftBuster.Backend.Registry;

namespace DriftBuster.Cli.Commands;

/// <summary>
/// <c>driftbuster registry-scan list-apps|suggest-roots|search|emit-config</c>: <c>python -m driftbuster.registry_cli</c> over
/// <see cref="RegistryCommands"/>, each line printed as the command returns it. Off Windows every subcommand exits 1 with
/// <c>Registry scanning requires Windows.</c> (checked once the arguments parse; Python checks before parsing).
/// </summary>
internal static class RegistryScanCommand
{
    private const string RootHelp = "Explicit hive path, e.g. HKLM\\Software\\Vendor[,view=64] (repeatable)";

    public static Command Build()
    {
        var command = new Command("registry-scan", "Windows Registry live scan helpers.");
        command.Subcommands.Add(BuildListApps());
        command.Subcommands.Add(BuildSuggestRoots());
        command.Subcommands.Add(BuildSearch());
        command.Subcommands.Add(BuildEmitConfig());
        return command;
    }

    private static int PrintLines(TextWriter stdout, IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            ConsoleText.Print(stdout, line);
        }

        return 0;
    }

    private static Command BuildListApps()
    {
        var command = new Command("list-apps", "List installed applications");
        command.SetAction(parseResult => CommandRunner.Run(parseResult, (stdout, _) => PrintLines(stdout, RegistryCommands.ListApps())));
        return command;
    }

    private static Command BuildSuggestRoots()
    {
        var token = PythonArguments.Positional("token", "App token, e.g. part of DisplayName or Publisher");
        var command = new Command("suggest-roots", "Suggest registry roots for an app token") { token };
        command.SetAction(parseResult => CommandRunner.Run(parseResult, (stdout, _) => PrintLines(stdout, RegistryCommands.SuggestRoots(parseResult.GetValue(token)!))));
        return command;
    }

    /// <summary>The options <c>search</c> and <c>emit-config</c> share.</summary>
    private sealed record SearchOptions(
        Option<string[]> Keyword,
        Option<string[]> Pattern,
        Option<BigInteger> MaxDepth,
        Option<BigInteger> MaxHits,
        Option<double> TimeBudget,
        Option<string[]> Root);

    private static SearchOptions AddSearchOptions(Command command)
    {
        var options = new SearchOptions(
            PythonArguments.Append("--keyword", "Keyword to require (repeatable)"),
            PythonArguments.Append("--pattern", "Regex to match (repeatable)"),
            PythonArguments.Int("--max-depth", 12, "Maximum key depth (default: 12)"),
            PythonArguments.Int("--max-hits", 200, "Maximum hits (default: 200)"),
            PythonArguments.Float("--time-budget", 10.0, "Time budget in seconds (default: 10.0)"),
            PythonArguments.Append("--root", RootHelp));
        command.Options.Add(options.Keyword);
        command.Options.Add(options.Pattern);
        command.Options.Add(options.MaxDepth);
        command.Options.Add(options.MaxHits);
        command.Options.Add(options.TimeBudget);
        return options;
    }

    private static Command BuildSearch()
    {
        var token = PythonArguments.Positional("token", "App token, e.g. part of DisplayName or Publisher");
        var command = new Command("search", "Search registry under suggested roots") { token };
        var options = AddSearchOptions(command);
        command.Options.Add(options.Root);
        command.SetAction(parseResult => CommandRunner.Run(parseResult, (stdout, _) => PrintLines(stdout, RegistryCommands.Search(
            parseResult.GetValue(token)!,
            parseResult.GetValue(options.Keyword),
            parseResult.GetValue(options.Pattern),
            parseResult.GetValue(options.MaxDepth),
            parseResult.GetValue(options.MaxHits),
            parseResult.GetValue(options.TimeBudget),
            parseResult.GetValue(options.Root)))));
        return command;
    }

    private static Command BuildEmitConfig()
    {
        var token = PythonArguments.Positional("token", "Token to feed into registry_scan entries");
        var alias = PythonArguments.OptionalText("--alias", "Optional alias for manifest output");
        var command = new Command("emit-config", "Render a registry_scan source snippet for remote or local runs") { token, alias };
        var options = AddSearchOptions(command);
        var remoteTarget = PythonArguments.Append(
            "--remote-target",
            "Remote host descriptor. Repeat to add a batch. Supported keys: username, password-env, credential-profile, transport, port, use-ssl, alias");
        remoteTarget.HelpName = "HOST[,key=value]...";
        command.Options.Add(remoteTarget);
        command.Options.Add(options.Root);
        command.SetAction(parseResult => CommandRunner.Run(parseResult, (stdout, _) =>
        {
            var snippet = RegistryCommands.EmitConfig(
                parseResult.GetValue(token)!,
                parseResult.GetValue(alias),
                parseResult.GetValue(options.Keyword),
                parseResult.GetValue(options.Pattern),
                parseResult.GetValue(options.MaxDepth),
                parseResult.GetValue(options.MaxHits),
                parseResult.GetValue(options.TimeBudget),
                parseResult.GetValue(remoteTarget),
                parseResult.GetValue(options.Root));
            ConsoleText.Print(stdout, RegistryCommands.EmitConfigJson(snippet));
            return 0;
        }));
        return command;
    }
}
