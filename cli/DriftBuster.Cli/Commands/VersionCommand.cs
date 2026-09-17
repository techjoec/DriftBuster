using System.CommandLine;

using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Cli.Commands;

/// <summary>
/// <c>driftbuster version</c>: <c>python scripts/sync_versions.py</c> for the checkout above the tool (<see cref="RepositoryRoot.Require"/>).
/// Exits 0 once every file is updated; a failed replacement or a <c>versions.json</c> without an expected key prints the reason on stderr
/// and exits 1.
/// </summary>
internal static class VersionCommand
{
    public static Command Build()
    {
        var command = new Command("version", "Synchronise project version strings from versions.json.");
        command.SetAction(parseResult => CommandRunner.Run(parseResult, (_, _) => Execute(RepositoryRoot.Require())));
        return command;
    }

    internal static int Execute(string root)
    {
        VersionSync.Run(root);
        return 0;
    }
}
