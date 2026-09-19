using System.CommandLine;
using System.Numerics;

using DriftBuster.Backend.Hunt;

namespace DriftBuster.Cli.Commands;

/// <summary>
/// <c>driftbuster hunt PATH</c>: the hits for the default rules printed as one JSON array (non-ASCII kept, keys sorted), each hit as the
/// JSON lines report writes it. The array is the hunt file <c>detection-profile hunt-bridge</c> reads. A file the hunt could not read
/// is skipped and named on stderr as <c>warning: unreadable file: {path}</c>.
/// </summary>
internal static class HuntCommand
{
    public static Command Build()
    {
        var path = EngineArguments.Positional("path", "File or directory to hunt.");
        var glob = EngineArguments.Text("--glob", "**/*", "Glob used when walking directories (default: **/*).");
        var sampleSize = EngineArguments.Int("--sample-size", HuntEngine.DefaultSampleSize, "Maximum bytes read from each file (default: 131072).");
        var exclude = EngineArguments.Append("--exclude", "Glob pattern matched against absolute and relative paths to skip (repeatable).");
        var template = EngineArguments.Text(
            "--placeholder-template", HuntEngine.DefaultPlaceholderTemplate, "Plan transform placeholder template, a .NET composite format string with one {token_name} field (default: {{{{ {token_name} }}}}, which renders as {{ name }}).");
        var command = new Command("hunt", "Hunt a file or directory for dynamic configuration values and print the hits as JSON.")
        {
            path, glob, sampleSize, exclude, template,
        };
        command.SetAction(parseResult => CommandRunner.Run(parseResult, (stdout, stderr) => Execute(
            parseResult.GetValue(path)!,
            parseResult.GetValue(glob)!,
            parseResult.GetValue(sampleSize),
            parseResult.GetValue(exclude)!,
            parseResult.GetValue(template)!,
            stdout,
            stderr)));
        return command;
    }

    internal static int Execute(string path, string glob, BigInteger sampleSize, IReadOnlyList<string> exclude, string placeholderTemplate, TextWriter stdout, TextWriter stderr)
    {
        var result = HuntEngine.HuntPath(path, HuntRules.Default, glob, (long)BigInteger.Clamp(sampleSize, long.MinValue, long.MaxValue), exclude);
        foreach (var unreadable in result.UnreadableFiles)
        {
            ConsoleText.Print(stderr, $"warning: unreadable file: {unreadable}");
        }

        var hits = HuntEngine.ToJson(result, placeholderTemplate).Cast<object?>().ToList();
        ConsoleText.Print(stdout, ConsoleText.Dumps(hits, indent: null, sortKeys: true, ensureAscii: false));
        return 0;
    }
}
