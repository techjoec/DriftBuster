using System.CommandLine;

using DriftBuster.Backend.Remote;

namespace DriftBuster.Cli.Commands;

/// <summary>
/// <c>driftbuster capture run|compare|export-sql</c> over <see cref="CaptureRunner"/>, which writes the commands' stdout and stderr text
/// itself.
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
        var profiles = EngineArguments.OptionalText("--profiles", "Path to ProfileStore JSON payload.");
        var profileTag = EngineArguments.Append("--profile-tag", "Optional profile tags to activate.");
        var glob = EngineArguments.Text("--glob", "**/*", "Glob used for scanning (defaults to **/*).");
        var huntGlob = EngineArguments.Text("--hunt-glob", "**/*", "Glob pattern for hunt traversal.");
        var huntExclude = EngineArguments.Append("--hunt-exclude", "Glob patterns to skip during hunt traversal.");
        var skipHunt = EngineArguments.Flag("--skip-hunt", "Skip hunt scan step.");
        var sampleSize = new Option<int>("--sample-size") { DefaultValueFactory = _ => 128 * 1024, Description = "Sample size in bytes for detection and hunt scans." };
        var outputDir = EngineArguments.Text("--output-dir", "captures", "Directory to store snapshot + manifest.");
        var captureId = EngineArguments.OptionalText("--capture-id", "Optional capture identifier (defaults to UTC timestamp).");
        var @operator = EngineArguments.OptionalText("--operator", "Operator name recorded in manifest.");
        var environment = EngineArguments.OptionalText("--environment", "Environment label (prod/test/etc).");
        var reason = EngineArguments.OptionalText("--reason", "Reason for this capture run.");
        var maskToken = EngineArguments.Append("--mask-token", "Sensitive token to redact (repeatable).");
        var placeholder = EngineArguments.Text("--placeholder", "[REDACTED]", "Placeholder string used for redaction.");
        var allowUnmasked = EngineArguments.Flag("--allow-unmasked", "Skip the redaction guard when no mask tokens are required.");
        var registryScan = EngineArguments.Append("--registry-scan", "Path to registry_scan.json outputs to embed in the manifest (repeatable).");
        var command = new Command("run", "Capture a snapshot and manifest.")
        {
            root, profiles, profileTag, glob, huntGlob, huntExclude, skipHunt, sampleSize, outputDir, captureId, @operator, environment, reason,
            maskToken, placeholder, allowUnmasked, registryScan,
        };
        command.SetAction(parseResult => CommandRunner.Run(parseResult, (stdout, stderr) => new CaptureRunner().Run(
            new CaptureRunOptions
            {
                Root = parseResult.GetValue(root)!,
                Profiles = parseResult.GetValue(profiles),
                ProfileTags = parseResult.GetValue(profileTag)!,
                Glob = parseResult.GetValue(glob)!,
                HuntGlob = parseResult.GetValue(huntGlob)!,
                HuntExclude = parseResult.GetValue(huntExclude)!,
                SkipHunt = parseResult.GetValue(skipHunt),
                SampleSize = parseResult.GetValue(sampleSize),
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
            stderr)));
        return command;
    }

    private static Command BuildCompare()
    {
        var baseline = EngineArguments.Positional("baseline", "Baseline snapshot JSON path.");
        var current = EngineArguments.Positional("current", "Current snapshot JSON path.");
        var command = new Command("compare", "Compare two capture snapshots.") { baseline, current };
        command.SetAction(parseResult => CommandRunner.Run(parseResult, (stdout, stderr) => new CaptureRunner().Compare(
            parseResult.GetValue(baseline)!, parseResult.GetValue(current)!, stdout, stderr)));
        return command;
    }

    private static Command BuildExportSql()
    {
        var command = new Command("export-sql", "Export anonymised SQL snapshots for portable review.");
        var arguments = new SqlExportArguments(command);
        command.SetAction(parseResult => CommandRunner.Run(parseResult, (stdout, stderr) => new CaptureRunner().ExportSql(arguments.Read(parseResult), stdout, stderr)));
        return command;
    }
}
