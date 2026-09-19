using System.Globalization;
using System.Text.Json;

using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Models;
using DriftBuster.Backend.MultiServer;

namespace DriftBuster.Cli.Commands;

/// <summary>
/// <c>driftbuster maint selfcheck-multi-server-paths</c>: runs the multi-server scenarios in process against
/// <c>&lt;portable root&gt;/Samples/MultiServer</c> or the repository's <c>fixtures/multi-server</c>, writes the JSON report, prints
/// <c>[PASS]</c> or <c>[FAIL]</c> per scenario and exits 0 only when every scenario passed.
/// </summary>
internal static class SelfcheckMultiServerPaths
{
    public const string DefaultPortableRoot = "artifacts/gui-packaging/portable/staged";

    public static string DefaultOutput(string root) => Path.Combine(root, "artifacts", "selfcheck", "multi_server_paths_report.json");

    /// <summary>The portable root's <c>Samples/MultiServer</c> when present, else the repository's multi-server fixtures.</summary>
    public static string ResolveSamples(string portableRoot, string root)
    {
        var portableSamples = Path.Join(portableRoot, "Samples", "MultiServer");
        if (Directory.Exists(portableSamples))
        {
            return portableSamples;
        }

        var fixtureSamples = Path.Join(root, "fixtures", "multi-server");
        return Directory.Exists(fixtureSamples) ? fixtureSamples : throw new CommandExitException("Could not locate multi-server sample directories.");
    }

    public static int Run(string portableRoot, string output, string root, TextWriter stdout, Func<string, string, IReadOnlyList<ScenarioResult>>? runScenarios = null)
    {
        var samplesRoot = ResolveSamples(portableRoot, root);
        var reportDir = Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!).FullName;
        var scenarios = (runScenarios ?? RunScenarios)(samplesRoot, reportDir);
        var report = new SelfcheckReport(DateTimeOffset.UtcNow, samplesRoot, scenarios.Count(scenario => scenario.Passed), scenarios.Count, scenarios);
        File.WriteAllText(output, JsonSerializer.Serialize(report, CliJsonContext.Default.SelfcheckReport) + "\n");
        foreach (var scenario in scenarios)
        {
            ConsoleText.Print(stdout, $"[{(scenario.Passed ? "PASS" : "FAIL")}] {scenario.Name}");
        }

        ConsoleText.Print(stdout, string.Create(CultureInfo.InvariantCulture, $"\nSelf-check summary: {report.Passed}/{report.Total} passed"));
        ConsoleText.Print(stdout, $"Report: {output}");
        return report.Success ? 0 : 1;
    }

    /// <summary>Every scenario in order, caches under <c>&lt;report dir&gt;/cache</c>.</summary>
    public static IReadOnlyList<ScenarioResult> RunScenarios(string samplesRoot, string reportDir)
    {
        var cacheRoot = Path.Join(reportDir, "cache");
        string Sample(string name) => Path.Join(samplesRoot, name);
        var server01 = Plan("host-01", Sample("server01"), preferred: true, priority: 10);
        var server02 = Plan("host-02", Sample("server02"), preferred: false, priority: 5);
        var scenarios = new List<ScenarioResult>
        {
            Scan("single_host", cacheRoot, [server01], response => response.Results.Length == 1 && response.Catalog.Length > 0 && response.Drilldown.Length > 0 && Failed(response) == 0),
            Scan("two_host_drift", cacheRoot, [server01, server02], response => response.Results.Length == 2 && response.Catalog.Any(entry => entry.DriftCount > 0)),
            Scan("missing_root_failure", cacheRoot, [server01, Plan("host-x", Sample("does-not-exist"), preferred: false, priority: 1)], response => Failed(response) >= 1),
            Scan("cache_reuse_hot_run", cacheRoot, [server01, server02], _ => true, cacheName: "hot-run"),
            Scan(
                "cache_reuse_hot_run_repeat",
                cacheRoot,
                [server01, server02],
                response => response.Results.Where(result => result.Availability == ServerAvailabilityStatus.Found).All(result => result.UsedCache),
                cacheName: "hot-run"),
        };

        var temp = Directory.CreateTempSubdirectory("driftbuster-selfcheck-");
        try
        {
            File.WriteAllText(Path.Combine(temp.FullName, "log4net.config"), "﻿<log4net><appender name='A'>☃</appender></log4net>");
            scenarios.Add(Scan("vendor_variant_unicode_payload", cacheRoot, [Plan("host-u", temp.FullName, preferred: true, priority: 1)], response => Failed(response) == 0 && response.Catalog.Length > 0));
        }
        finally
        {
            temp.Delete(recursive: true);
        }

        using var stdout = new StringWriter(CultureInfo.InvariantCulture);
        var exitCode = MultiServerCommand.Execute(new StringReader("""{"schema_version": "multi-server.v0", "plans": []}"""), stdout);
        scenarios.Add(new ScenarioResult("invalid_schema_rejected", exitCode == 1 && stdout.ToString().Contains("Unsupported schema version", StringComparison.Ordinal), stdout.ToString().Trim()));
        return scenarios;
    }

    private static ScenarioResult Scan(string name, string cacheRoot, IReadOnlyList<ServerScanPlan> plans, Func<ServerScanResponse, bool> judge, string? cacheName = null)
    {
        try
        {
            var runner = new MultiServerRunner(Directory.CreateDirectory(Path.Join(cacheRoot, cacheName ?? name)).FullName);
            var response = runner.Run(plans.Select(MultiServerPlan.FromServerScanPlan));
            var details = string.Create(
                CultureInfo.InvariantCulture,
                $"hosts={response.Results.Length} catalog={response.Catalog.Length} drilldown={response.Drilldown.Length} failed={Failed(response)}");
            return new ScenarioResult(name, judge(response), details);
        }
        catch (Exception exc) when (exc is not OutOfMemoryException)
        {
            return new ScenarioResult(name, false, $"{exc.GetType().Name}: {exc.Message}");
        }
    }

    private static int Failed(ServerScanResponse response) => response.Results.Count(result => result.Status == ServerScanStatus.Failed);

    private static ServerScanPlan Plan(string hostId, string root, bool preferred, int priority) => new()
    {
        HostId = hostId,
        Label = hostId,
        Scope = ServerScanScope.CustomRoots,
        Roots = [root],
        Baseline = new ServerScanBaselinePreference { IsPreferred = preferred, Priority = priority },
    };
}
