using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Cli.Commands;

/// <summary>
/// <c>driftbuster maint selfcheck-multi-server-paths</c>, <c>python scripts/selfcheck_multi_server_paths.py</c> without Python: runs the
/// multi-server scenarios in process through <see cref="MultiServerCommand.Execute"/> against <c>&lt;portable root&gt;/Samples/MultiServer</c>
/// or the repository's <c>fixtures/multi-server</c>, writes the JSON report, prints <c>[PASS]</c> or <c>[FAIL]</c> per scenario and exits 0
/// only when every scenario passed.
/// </summary>
internal static partial class SelfcheckMultiServerPaths
{
    public const string DefaultPortableRoot = "/lap_temp/DriftBuster-Portabletest";

    public static string DefaultOutput(string root) => Path.Combine(root, "artifacts", "selfcheck", "multi_server_paths_report.json");

    /// <summary><c>resolve_samples(portable_root)</c>.</summary>
    public static string ResolveSamples(string portableRoot, string root)
    {
        var portableSamples = PythonPurePath.Join(PythonPurePath.Join(portableRoot, "Samples"), "MultiServer");
        if (TextModeFile.Exists(portableSamples))
        {
            return portableSamples;
        }

        var fixtureSamples = Path.Combine(root, "fixtures", "multi-server");
        return TextModeFile.Exists(fixtureSamples)
            ? fixtureSamples
            : throw new CommandExitException("Could not locate multi-server sample directories.");
    }

    public static int Run(
        string portableRoot, string output, string root, TextWriter stdout, Func<string, string, IReadOnlyList<ScenarioResult>>? runScenarios = null)
    {
        runScenarios ??= RunScenarios;
        var samplesRoot = ResolveSamples(portableRoot, root);
        var reportPath = PythonPurePath.Str(output);
        var reportDir = PythonPurePath.Parent(reportPath);
        Directory.CreateDirectory(reportDir);

        var scenarios = runScenarios(samplesRoot, reportDir);
        var passed = scenarios.LongCount(scenario => PythonBuiltins.IsTruthy(scenario.Passed));
        var total = (long)scenarios.Count;
        var report = new OrderedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["generated_at"] = PythonDateTime.UtcNow().IsoFormat(),
            ["samples_root"] = PythonPurePath.Str(samplesRoot),
            ["passed"] = passed,
            ["total"] = total,
            ["success"] = passed == total,
            ["scenarios"] = scenarios.Select(scenario => (object?)new OrderedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["name"] = scenario.Name,
                ["passed"] = scenario.Passed,
                ["details"] = scenario.Details,
            }).ToList(),
        };
        TextModeFile.WriteText(reportPath, ConsoleText.Dumps(report, indent: 2, sortKeys: false) + "\n");

        foreach (var scenario in scenarios)
        {
            ConsoleText.Print(stdout, $"[{(PythonBuiltins.IsTruthy(scenario.Passed) ? "PASS" : "FAIL")}] {scenario.Name}");
        }

        ConsoleText.Print(stdout, $"\nSelf-check summary: {passed}/{total} passed");
        ConsoleText.Print(stdout, $"Report: {reportPath}");
        return passed == total ? 0 : 1;
    }

    /// <summary>
    /// <c>run_multi_server(request=...)</c>: the exit code, the parsed JSON lines (a line that is not JSON becomes a <c>decode-error</c>
    /// event) and the stripped stderr, which the in-process run leaves empty.
    /// </summary>
    public static (int ReturnCode, List<object?> Events, string Stderr) RunMultiServer(OrderedDictionary<string, object?> request)
    {
        using var stdin = new StringReader(ConsoleText.Dumps(request, indent: null, sortKeys: false));
        using var stdout = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        var returnCode = MultiServerCommand.Execute(stdin, stdout);
        var events = new List<object?>();
        foreach (var raw in TextLines.SplitLines(stdout.ToString()))
        {
            var line = PythonText.Strip(raw);
            if (line.Length == 0)
            {
                continue;
            }

            events.Add(PythonJson.TryLoads(line, out var parsed)
                ? parsed
                : new OrderedDictionary<string, object?>(StringComparer.Ordinal) { ["type"] = "decode-error", ["line"] = line });
        }

        return (returnCode, events, string.Empty);
    }
}
