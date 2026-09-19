using System.CommandLine;

using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Cli.Commands;

/// <summary>
/// <c>driftbuster maint</c>: <c>selfcheck-multi-server-paths</c> (<see cref="SelfcheckMultiServerPaths"/>) and
/// <c>purge-reporting-retention</c> (<see cref="PurgeReportingRetention"/>).
/// </summary>
internal static class MaintCommand
{
    public static Command Build() => new("maint", "Maintenance commands.") { BuildSelfcheck(), BuildPurge() };

    private static Command BuildSelfcheck()
    {
        var portableRoot = CliOptions.Text(
            "--portable-root",
            SelfcheckMultiServerPaths.DefaultPortableRoot,
            $"Portable root whose Samples/MultiServer is used (default: {SelfcheckMultiServerPaths.DefaultPortableRoot}).");
        var output = CliOptions.OptionalText("--output", "Path to write JSON report (default: artifacts/selfcheck/multi_server_paths_report.json).");
        var command = new Command("selfcheck-multi-server-paths", "Run multi-server end-to-end self-checks against sample configs.") { portableRoot, output };
        command.SetAction(parseResult => CommandRunner.Run(parseResult, (stdout, _) =>
        {
            var root = RepositoryRoot.Require();
            return SelfcheckMultiServerPaths.Run(
                parseResult.GetValue(portableRoot)!,
                parseResult.GetValue(output) ?? SelfcheckMultiServerPaths.DefaultOutput(root),
                root,
                stdout);
        }));
        return command;
    }

    private static Command BuildPurge()
    {
        var paths = new Argument<string[]>("PATH") { Arity = ArgumentArity.OneOrMore, Description = "Directories or files to evaluate for retention purge" };
        var retentionDays = CliOptions.Int("--retention-days", 30, "Retention window in days (default: 30)");
        var confirm = CliOptions.Flag("--confirm", "Actually delete candidates; otherwise prints a dry-run report");
        var command = new Command("purge-reporting-retention", "Delete reporting artefacts older than the retention window.") { paths, retentionDays, confirm };
        command.SetAction(parseResult => CommandRunner.Run(parseResult, (stdout, _) => PurgeReportingRetention.Run(
            parseResult.GetValue(paths)!,
            parseResult.GetValue(retentionDays),
            parseResult.GetValue(confirm),
            stdout)));
        return command;
    }
}
