using System.CommandLine;

using DriftBuster.Backend.Remote;

namespace DriftBuster.Cli.Commands;

/// <summary>
/// <c>driftbuster sql-export DATABASE...</c> over <see cref="CaptureRunner.RunSqlExport"/>, with <c>--manifest-name</c>, the positive
/// <c>--limit</c> check and the <c>Manifest written to</c> line that <c>driftbuster capture export-sql</c> does not print.
/// </summary>
internal static class SqlExportCommand
{
    public static Command Build()
    {
        var command = new Command("sql-export", "Export anonymised SQL snapshots for portable review.");
        var arguments = new SqlExportArguments(command);
        var manifestName = EngineArguments.Text("--manifest-name", "sql-manifest.json", "Filename for the manifest written to the output directory.");
        command.Options.Add(manifestName);
        command.SetAction(parseResult => CommandRunner.Run(parseResult, (stdout, stderr) =>
        {
            var options = arguments.Read(parseResult) with
            {
                ManifestName = parseResult.GetValue(manifestName)!,
                LimitMustBePositive = true,
                ReportManifestPath = true,
            };
            return CaptureRunner.RunSqlExport(options, stdout, stderr).ExitCode;
        }));
        return command;
    }
}
