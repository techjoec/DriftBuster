using System.CommandLine;

using DriftBuster.Backend;
using DriftBuster.Backend.Models;

namespace DriftBuster.Cli.Commands;

/// <summary>
/// <c>driftbuster report [ROOT]</c>: the detections of a file or directory, with the default hunt rules' hits unless <c>--skip-hunt</c>,
/// rendered through <see cref="IDriftbusterBackend.BuildReportAsync"/> as an HTML page or JSON lines (<c>--format jsonl</c>). Without
/// <c>--output</c> the report goes to stdout; with it the file is written with platform line breaks and <c>Report written to {path}</c>
/// is printed.
/// </summary>
internal static class ReportCommand
{
    public static Command Build()
    {
        var root = new Argument<string>("root") { Arity = ArgumentArity.ZeroOrOne, DefaultValueFactory = _ => ".", Description = "File or directory to scan." };
        var format = CliOptions.Text("--format", "html", "Report format: html or jsonl (default: html).");
        format.AcceptOnlyFromAmong("html", "jsonl");
        var glob = CliOptions.Text("--glob", "**/*", "Glob used when scanning and hunting directories (default: **/*).");
        var skipHunt = CliOptions.Flag("--skip-hunt", "Leave hunt hits out of the report.");
        var title = CliOptions.Text("--title", "DriftBuster Report", "HTML report title.");
        var maskToken = CliOptions.Append("--mask-token", "Sensitive token to redact (repeatable).");
        var placeholder = CliOptions.Text("--placeholder", "[REDACTED]", "Placeholder string used for redaction.");
        var output = CliOptions.OptionalText("--output", "File the report is written to (defaults to stdout).");
        var command = new Command("report", "Render an HTML or JSON lines report of a scanned tree.")
        {
            root, format, glob, skipHunt, title, maskToken, placeholder, output,
        };
        command.SetAction(parseResult => CommandRunner.Run(parseResult, (stdout, _) => Execute(
            new ReportRequest
            {
                Root = parseResult.GetValue(root)!,
                Format = parseResult.GetValue(format)!,
                Glob = parseResult.GetValue(glob)!,
                IncludeHunt = !parseResult.GetValue(skipHunt),
                Title = parseResult.GetValue(title)!,
                MaskTokens = parseResult.GetValue(maskToken)!,
                Placeholder = parseResult.GetValue(placeholder)!,
                OutputPath = parseResult.GetValue(output),
            },
            stdout)));
        return command;
    }

    internal static int Execute(ReportRequest request, TextWriter stdout)
    {
        IDriftbusterBackend backend = new DriftbusterBackend();
        var result = backend.BuildReportAsync(request).GetAwaiter().GetResult();
        ConsoleText.Write(stdout, result.OutputPath is null ? result.Content : $"Report written to {result.OutputPath}\n");
        return 0;
    }
}
