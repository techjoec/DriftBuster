using System.CommandLine;
using System.Numerics;

using DriftBuster.Backend.Remote;

namespace DriftBuster.Cli.Commands;

/// <summary>
/// <c>driftbuster capture run|compare|export-sql</c>: <c>scripts/capture.py</c> over <see cref="CaptureRunner"/>, which writes the
/// commands' stdout and stderr text itself.
/// </summary>
internal static class CaptureCommand
{
    public static Command Build()
    {
        var command = new Command("capture", "Manual capture helper for driftbuster runs.");
        command.Subcommands.Add(BuildRun());
        command.Subcommands.Add(BuildCompare());
        command.Subcommands.Add(BuildExportSql());
        return command;
    }

    private static Command BuildRun()
    {
        var root = new Argument<string>("root") { Arity = ArgumentArity.ZeroOrOne, DefaultValueFactory = _ => ".", Description = "Directory to scan." };
        var profiles = PythonArguments.OptionalText("--profiles", "Path to ProfileStore JSON payload.");
        var profileTag = PythonArguments.Append("--profile-tag", "Optional profile tags to activate.");
        var glob = PythonArguments.Text("--glob", "**/*", "Glob used for scanning (defaults to **/*).");
        var huntGlob = PythonArguments.Text("--hunt-glob", "**/*", "Glob pattern for hunt traversal.");
        var huntExclude = PythonArguments.Append("--hunt-exclude", "Glob patterns to skip during hunt traversal.");
        var skipHunt = PythonArguments.Flag("--skip-hunt", "Skip hunt scan step.");
        var sampleSize = PythonArguments.Int("--sample-size", 128 * 1024, "Sample size in bytes for detection and hunt scans.");
        var outputDir = PythonArguments.Text("--output-dir", "captures", "Directory to store snapshot + manifest.");
        var captureId = PythonArguments.OptionalText("--capture-id", "Optional capture identifier (defaults to UTC timestamp).");
        var @operator = PythonArguments.OptionalText("--operator", "Operator name recorded in manifest.");
        var environment = PythonArguments.OptionalText("--environment", "Environment label (prod/test/etc).");
        var reason = PythonArguments.OptionalText("--reason", "Reason for this capture run.");
        var maskToken = PythonArguments.Append("--mask-token", "Sensitive token to redact (repeatable).");
        var placeholder = PythonArguments.Text("--placeholder", "[REDACTED]", "Placeholder string used for redaction.");
        var allowUnmasked = PythonArguments.Flag("--allow-unmasked", "Skip the redaction guard when no mask tokens are required.");
        var registryScan = PythonArguments.Append("--registry-scan", "Path to registry_scan.json outputs to embed in the manifest (repeatable).");
        var command = new Command("run", "Capture a snapshot and manifest.")
        {
            root, profiles, profileTag, glob, huntGlob, huntExclude, skipHunt, sampleSize, outputDir, captureId, @operator, environment, reason,
            maskToken, placeholder, allowUnmasked, registryScan,
        };
        command.SetAction(parseResult => CommandRunner.Run(parseResult, (stdout, stderr) => CaptureRunner.RunCapture(
            new CaptureRunOptions
            {
                Root = parseResult.GetValue(root)!,
                Profiles = parseResult.GetValue(profiles),
                ProfileTags = parseResult.GetValue(profileTag)!,
                Glob = parseResult.GetValue(glob)!,
                HuntGlob = parseResult.GetValue(huntGlob)!,
                HuntExclude = parseResult.GetValue(huntExclude)!,
                SkipHunt = parseResult.GetValue(skipHunt),
                SampleSize = (long)BigInteger.Clamp(parseResult.GetValue(sampleSize), long.MinValue, long.MaxValue),
                OutputDir = parseResult.GetValue(outputDir)!,
                CaptureId = parseResult.GetValue(captureId),
                Operator = parseResult.GetValue(@operator),
                Environment = parseResult.GetValue(environment),
                Reason = parseResult.GetValue(reason),
                MaskTokens = parseResult.GetValue(maskToken)!,
                Placeholder = parseResult.GetValue(placeholder)!,
                AllowUnmasked = parseResult.GetValue(allowUnmasked),
                RegistryScan = parseResult.GetValue(registryScan)!,
            },
            stdout,
            stderr).ExitCode));
        return command;
    }

    private static Command BuildCompare()
    {
        var baseline = PythonArguments.Positional("baseline", "Baseline snapshot JSON path.");
        var current = PythonArguments.Positional("current", "Current snapshot JSON path.");
        var command = new Command("compare", "Compare two capture snapshots.") { baseline, current };
        command.SetAction(parseResult => CommandRunner.Run(parseResult, (stdout, stderr) => CaptureRunner.CompareSnapshots(
            new CaptureCompareOptions(parseResult.GetValue(baseline)!, parseResult.GetValue(current)!), stdout, stderr).ExitCode));
        return command;
    }

    private static Command BuildExportSql()
    {
        var command = new Command("export-sql", "Export anonymised SQL snapshots for portable review.");
        var arguments = new SqlExportArguments(command);
        command.SetAction(parseResult => CommandRunner.Run(parseResult, (stdout, stderr) => CaptureRunner.RunSqlExport(arguments.Read(parseResult), stdout, stderr).ExitCode));
        return command;
    }
}
