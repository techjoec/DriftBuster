using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Cli.Commands;

internal static partial class SelfcheckMultiServerPaths
{
    /// <summary><c>run_scenarios(samples_root, pythonpath, report_dir)</c>: every scenario in order, caches under <c>&lt;report dir&gt;/cache</c>.</summary>
    public static IReadOnlyList<ScenarioResult> RunScenarios(string samplesRoot, string reportDir)
    {
        var scenarios = new List<ScenarioResult>();
        var cacheDir = PythonPurePath.Join(reportDir, "cache");
        Directory.CreateDirectory(cacheDir);
        string Cache(string name) => PythonPurePath.Join(cacheDir, name);
        string Sample(string name) => PythonPurePath.Join(samplesRoot, name);
        var server01 = MakePlan("host-01", "server01", Sample("server01"), preferred: true, priority: 10);
        var server02 = MakePlan("host-02", "server02", Sample("server02"), preferred: false, priority: 5);

        void Execute(string name, OrderedDictionary<string, object?> request, Func<OrderedDictionary<string, object?>, object?> judge)
        {
            var (returnCode, events, stderr) = RunMultiServer(request);
            var details = EvaluateResponse(name, returnCode, events, stderr);
            scenarios.Add(new ScenarioResult(name, judge(details), details));
        }

        Execute("single_host", Request(Cache("single"), server01), d => Number(d, "returncode") == 0 && Is(d, "has_result")
            && Number(d, "hosts") == 1 && Number(d, "catalog") > 0 && Number(d, "drilldown") > 0 && Number(d, "failed_hosts") == 0);
        Execute("two_host_drift", Request(Cache("drift"), server01, server02), d => Number(d, "returncode") == 0 && Is(d, "has_result")
            && Number(d, "hosts") == 2 && Number(d, "drift_entries") > 0);
        Execute(
            "missing_root_failure",
            Request(Cache("missing"), server01, MakePlan("host-x", "missing", Sample("does-not-exist"), preferred: false, priority: 1)),
            d => Number(d, "returncode") == 0 && Is(d, "has_result") && Number(d, "failed_hosts") >= 1);
        Execute(
            "all_drives_scope",
            Request(Cache("all-drives"), MakePlan("host-01", "server01", Sample("server01"), preferred: true, priority: 10, scope: "all_drives")),
            d => Number(d, "returncode") == 0 && Is(d, "has_result") && Number(d, "hosts") == 1 && Number(d, "catalog") > 0);
        Execute("cache_reuse_hot_run", Request(Cache("hot-run"), server01, server02), d => Number(d, "returncode") == 0);
        Execute("cache_reuse_hot_run_repeat", Request(Cache("hot-run"), server01, server02), d => Number(d, "returncode") == 0 && Is(d, "has_result")
            && PythonBuiltins.Iterate(PythonBuiltins.Get(d["payload"], "results") ?? new List<object?>())
                .Where(entry => PythonBuiltins.Get(entry, "availability") is "found")
                .All(entry => PythonBuiltins.IsTruthy(PythonBuiltins.Get(entry, "used_cache"))));

        var temp = Directory.CreateTempSubdirectory("driftbuster-selfcheck-");
        try
        {
            TextModeFile.WriteText(Path.Combine(temp.FullName, "log4net.config"), "﻿<log4net><appender name='A'>☃</appender></log4net>");
            Execute(
                "vendor_variant_unicode_payload",
                Request(Cache("vendor-unicode"), MakePlan("host-u", "unicode", temp.FullName, preferred: true, priority: 1)),
                d => Number(d, "returncode") == 0 && Is(d, "has_result") && Number(d, "failed_hosts") == 0 && Number(d, "catalog") > 0);
        }
        finally
        {
            temp.Delete(recursive: true);
        }

        Execute("invalid_schema_rejected", SchemaRequest(Cache("schema")), SchemaJudge);
        return scenarios;
    }

    private static OrderedDictionary<string, object?> SchemaRequest(string cacheDir)
        => new(StringComparer.Ordinal) { ["schema_version"] = "multi-server.v0", ["cache_dir"] = cacheDir, ["plans"] = new List<object?>() };

    // d["returncode"] == 1 and d["error_message"] and "Unsupported schema version" in d["error_message"]
    private static object? SchemaJudge(OrderedDictionary<string, object?> details)
    {
        if (Number(details, "returncode") != 1)
        {
            return false;
        }

        var message = details["error_message"];
        return !PythonBuiltins.IsTruthy(message)
            ? message
            : PythonText.Contains(PythonRepr.Str(message), "Unsupported schema version");
    }
}
