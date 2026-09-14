using System.CommandLine;
using System.Text.Json;

using DriftBuster.Cli;

namespace DriftBuster.Cli.Tests;

/// <summary>
/// The multi-server surface of <c>parity-dump</c>. Expectations come from tools/parity/py_dump.py multi-server over the same
/// tree (with PARITY_MULTI_SERVER_FIXES=1), whose output the port's matched byte for byte.
/// </summary>
public sealed class ParityDumpMultiServerTests : IDisposable
{
    // py_dump.py _key_order of the dumped record for two hosts, one config each, two cache entries.
    private const string TwoHostKeyOrder =
            "[[\"progress\",[[[\"host_id\",null],[\"status\",null],[\"message\",null]],[[\"host_id\",null],[\"status\","
            + "null],[\"message\",null]],[[\"host_id\",null],[\"status\",null],[\"message\",null]],[[\"host_id\",null],"
            + "[\"status\",null],[\"message\",null]]]],[\"response\",[[\"version\",null],[\"results\",[[[\"host_id\",null],"
            + "[\"label\",null],[\"status\",null],[\"message\",null],[\"timestamp\",null],[\"roots\",[null]],[\"used_cache\","
            + "null],[\"availability\",null],[\"sampling_guardrail_triggered\",null]],[[\"host_id\",null],[\"label\",null],"
            + "[\"status\",null],[\"message\",null],[\"timestamp\",null],[\"roots\",[null]],[\"used_cache\",null],"
            + "[\"availability\",null],[\"sampling_guardrail_triggered\",null]]]],[\"catalog\",[[[\"config_id\",null],"
            + "[\"display_name\",null],[\"format\",null],[\"drift_count\",null],[\"severity\",null],[\"present_hosts\",[null,"
            + "null]],[\"missing_hosts\",[]],[\"last_updated\",null],[\"has_secrets\",null],[\"has_masked_tokens\",null],"
            + "[\"has_validation_issues\",null],[\"coverage_status\",null]]]],[\"drilldown\",[[[\"config_id\",null],"
            + "[\"display_name\",null],[\"format\",null],[\"servers\",[[[\"host_id\",null],[\"label\",null],[\"present\","
            + "null],[\"is_baseline\",null],[\"status\",null],[\"drift_lines\",null],[\"has_secrets\",null],[\"masked\","
            + "null],[\"redaction_status\",null],[\"last_seen\",null],[\"presence_status\",null]],[[\"host_id\",null],"
            + "[\"label\",null],[\"present\",null],[\"is_baseline\",null],[\"status\",null],[\"drift_lines\",null],"
            + "[\"has_secrets\",null],[\"masked\",null],[\"redaction_status\",null],[\"last_seen\",null],"
            + "[\"presence_status\",null]]]],[\"baseline_host_id\",null],[\"diff_before\",null],[\"diff_after\",null],"
            + "[\"unified_diff\",null],[\"diff_summary\",[[\"generated_at\",null],[\"versions\",[null,null]],"
            + "[\"comparison_count\",null],[\"comparisons\",[[[\"from\",null],[\"to\",null],[\"plan\",[[\"content_type\","
            + "null],[\"from_label\",null],[\"to_label\",null],[\"label\",null],[\"mask_tokens\",[]],[\"placeholder\",null],"
            + "[\"context_lines\",null],[\"redaction_counts\",[]],[\"binary_evidence\",[]],[\"safety_limits\",null]]],"
            + "[\"metadata\",[[\"content_type\",null],[\"context_lines\",null],[\"baseline_name\",null],[\"comparison_name\","
            + "null]]],[\"summary\",[[\"before_digest\",null],[\"after_digest\",null],[\"diff_digest\",null],"
            + "[\"before_lines\",null],[\"after_lines\",null],[\"added_lines\",null],[\"removed_lines\",null],"
            + "[\"changed_lines\",null]]]]]]]],[\"has_secrets\",null],[\"has_masked_tokens\",null],"
            + "[\"has_validation_issues\",null],[\"notes\",[null]],[\"provenance\",null],[\"drift_count\",null],"
            + "[\"last_updated\",null]]]],[\"summary\",[[\"baseline_host_id\",null],[\"total_hosts\",null],"
            + "[\"configs_evaluated\",null],[\"drifting_configs\",null],[\"generated_at\",null]]]]],[\"cache\",[[[\"file\","
            + "null],[\"signature\",null],[\"sha256\",null]],[[\"file\",null],[\"signature\",null],[\"sha256\",null]]]]]";

    private readonly DirectoryInfo _tmp = Directory.CreateTempSubdirectory("driftbuster-parity-multi-server-");

    public void Dispose() => _tmp.Delete(recursive: true);

    private string Tree(string relative, string content)
    {
        var path = Path.Combine([_tmp.FullName, .. relative.Split('/')]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return Path.GetDirectoryName(path)!;
    }

    private string Plans()
    {
        var hostA = Tree("a/app.json", "{\"Mode\": \"on\"}\n");
        var hostB = Tree("b/app.json", "{\"Mode\": \"off\"}\n");
        var plans = new
        {
            plans = new object[]
            {
                new { host_id = "a", label = "A", roots = new[] { hostA }, baseline = new { is_preferred = true } },
                new { host_id = "b", label = "B", roots = new[] { hostB, Path.Combine(_tmp.FullName, "missing") } },
            },
        };
        var path = Path.Combine(_tmp.FullName, "plans.json");
        File.WriteAllText(path, JsonSerializer.Serialize(plans));
        return path;
    }

    private static JsonElement Invoke(params string[] args)
    {
        var output = new StringWriter();
        var configuration = new InvocationConfiguration { Output = output, Error = new StringWriter() };
        Program.BuildRootCommand().Parse(args).Invoke(configuration).Should().Be(0);
        var lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines.Should().ContainSingle();
        return JsonDocument.Parse(lines[0]).RootElement.Clone();
    }

    [Fact]
    public void MultiServer_prints_progress_response_and_cache_in_the_python_shape()
    {
        var plans = Plans();

        var document = Invoke("parity-dump", "multi-server", plans);

        JsonSerializer.Serialize(document.GetProperty("key_order")).Should().Be(TwoHostKeyOrder);
        document.GetProperty("progress").EnumerateArray()
            .Select(entry => $"{entry.GetProperty("host_id").GetString()}|{entry.GetProperty("status").GetString()}|{entry.GetProperty("message").GetString()}")
            .Should().Equal("a|running|Scanning A", "a|succeeded|Evaluated 1 configuration(s).", "b|running|Scanning B", "b|succeeded|Evaluated 1 configuration(s).");
        var response = document.GetProperty("response");
        var results = response.GetProperty("results").EnumerateArray().ToList();
        results.Select(result => result.GetProperty("roots")[0].GetString())
            .Should().Equal(Path.GetRelativePath(Environment.CurrentDirectory, Path.Combine(_tmp.FullName, "a")),
                Path.GetRelativePath(Environment.CurrentDirectory, Path.Combine(_tmp.FullName, "b")));
        results.Should().AllSatisfy(result => result.GetProperty("timestamp").GetString().Should().Be("<timestamp>"));
        response.GetProperty("summary").GetProperty("generated_at").GetString().Should().Be("<timestamp>");
        var drilldown = response.GetProperty("drilldown")[0];
        drilldown.GetProperty("diff_summary").GetProperty("generated_at").GetString().Should().Be("<timestamp>");
        drilldown.GetProperty("servers").EnumerateArray().Should().AllSatisfy(server => server.GetProperty("last_seen").GetString().Should().Be("<timestamp>"));
        response.GetProperty("catalog")[0].GetProperty("config_id").GetString().Should().Be("json/generic/app-json");
        document.GetProperty("cache").EnumerateArray().Select(entry => entry.GetProperty("file").GetString())
            .Should().Equal("8aba90966f86f3966bb49b78e1ace757d7436487.json", "f4059723a7a1357c336a098ac0f9845054686be6.json");
    }

    [Fact]
    public void MultiServer_second_run_against_one_cache_reports_used_cache()
    {
        var plans = Plans();

        var document = Invoke("parity-dump", "multi-server", plans, "--runs", "2", "--sample-budget", "4096", "--sample-size", "1024");

        document.GetProperty("response").GetProperty("results").EnumerateArray()
            .Should().AllSatisfy(result => result.GetProperty("used_cache").GetBoolean().Should().BeTrue());
        document.GetProperty("progress").GetArrayLength().Should().Be(4);
    }

    // multi_server._build_plans coerces the request (str() of a numeric host id, int() of a priority string); the dump decodes it
    // the same way instead of binding the GUI's typed plan model.
    [Fact]
    public void MultiServer_decodes_the_request_with_python_plan_coercions()
    {
        var hostB = Tree("b/app.json", "{\"Mode\": \"off\"}\n");
        var path = Path.Combine(_tmp.FullName, "coerced.json");
        var missing = JsonSerializer.Serialize(Path.Combine(_tmp.FullName, "missing"));
        File.WriteAllText(
            path,
            "{\"plans\": [7, {\"host_id\": 42, \"roots\": {" + missing + ": 1}, \"scope\": \"network share\", \"cached_at\": 1},"
            + " {\"host_id\": \"b\", \"roots\": [" + JsonSerializer.Serialize(hostB) + "], \"baseline\": {\"is_preferred\": \"false\", \"priority\": \" 7 \"}}]}");

        var document = Invoke("parity-dump", "multi-server", path);

        var response = document.GetProperty("response");
        response.GetProperty("results").EnumerateArray().Select(result => result.GetProperty("host_id").GetString()).Should().Equal("42", "b");
        response.GetProperty("results")[0].GetProperty("availability").GetString().Should().Be("not_found");
        response.GetProperty("summary").GetProperty("baseline_host_id").GetString().Should().Be("b");
    }

    // py_dump.py _escape_lone_surrogates: an unpaired surrogate in a model string (a host id, a label, the progress naming them)
    // is the literal text \uXXXX in the dump, never the U+FFFD the runtime's JSON writer would put there.
    [Fact]
    public void MultiServer_spells_unpaired_surrogates_in_model_strings_as_python_does()
    {
        var hostA = Tree("a/app.ini", "[core]\nname = a\n");
        var path = Path.Combine(_tmp.FullName, "surrogates.json");
        File.WriteAllText(path, "{\"plans\": [{\"host_id\": \"host\\ud800A\", \"label\": \"label\\udfff\", \"roots\": [" + JsonSerializer.Serialize(hostA) + "]}]}");
        var output = new StringWriter();
        var configuration = new InvocationConfiguration { Output = output, Error = new StringWriter() };

        Program.BuildRootCommand().Parse(["parity-dump", "multi-server", path]).Invoke(configuration).Should().Be(0);

        var response = JsonDocument.Parse(output.ToString()).RootElement.GetProperty("response");
        response.GetProperty("results")[0].GetProperty("host_id").GetString().Should().Be("host\\ud800A");
        response.GetProperty("results")[0].GetProperty("label").GetString().Should().Be("label\\udfff");
        response.GetProperty("drilldown")[0].GetProperty("servers")[0].GetProperty("label").GetString().Should().Be("label\\udfff");
    }

    [Fact]
    public void MultiServer_aborts_with_pythons_error_for_a_request_python_rejects()
    {
        var path = Path.Combine(_tmp.FullName, "rejected.json");
        File.WriteAllText(path, """{"plans": [{"host_id": "a", "roots": ["x"], "baseline": {"priority": "4.5"}}]}""");
        var error = new StringWriter();
        var configuration = new InvocationConfiguration { Output = new StringWriter(), Error = error };

        var exit = Program.BuildRootCommand().Parse(["parity-dump", "multi-server", path]).Invoke(configuration);

        exit.Should().NotBe(0);
        error.ToString().Should().Contain("invalid literal for int() with base 10: '4.5'");
    }

    [Fact]
    public void ReplaceTimestamps_marks_a_stamp_that_is_not_utc()
    {
        var value = new OrderedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["timestamp"] = "2026-01-01T00:00:00+01:00",
            ["last_updated"] = "2026-01-01T00:00:00.1234+00:00",
            ["generated_at"] = "2026-01-01T00:00:00.123400Z",
            ["nested"] = new List<object?>
            {
                new OrderedDictionary<string, object?>(StringComparer.Ordinal) { ["last_seen"] = "2026-01-01T00:00:00.500000+00:00", ["other"] = "x", ["timestamp"] = null },
            },
        };

        var replaced = (OrderedDictionary<string, object?>)ParityDump.ReplaceTimestamps(value)!;

        replaced["timestamp"].Should().Be("<invalid timestamp: 2026-01-01T00:00:00+01:00>");
        replaced["last_updated"].Should().Be("<invalid timestamp: 2026-01-01T00:00:00.1234+00:00>");
        replaced["generated_at"].Should().Be("<invalid timestamp: 2026-01-01T00:00:00.123400Z>");
        var nested = (OrderedDictionary<string, object?>)((List<object?>)replaced["nested"]!)[0]!;
        nested["last_seen"].Should().Be("<timestamp>");
        nested["other"].Should().Be("x");
        nested["timestamp"].Should().BeNull();
    }

    [Fact]
    public void SpellTimestamp_writes_isoformat_only_for_values_datetime_now_utc_can_return()
    {
        var whole = new DateTimeOffset(2026, 9, 13, 10, 0, 0, TimeSpan.Zero);

        ParityDump.SpellTimestamp(whole).Should().Be("2026-09-13T10:00:00+00:00");
        ParityDump.SpellTimestamp(whole.AddTicks(1_234_000)).Should().Be("2026-09-13T10:00:00.123400+00:00");
        ParityDump.SpellTimestamp(whole.AddTicks(10)).Should().Be("2026-09-13T10:00:00.000001+00:00");
        ParityDump.SpellTimestamp(whole.AddTicks(1)).Should().Be("2026-09-13T10:00:00.0000001+00:00");
        ParityDump.SpellTimestamp(whole.ToOffset(TimeSpan.FromHours(-5))).Should().Be("2026-09-13T05:00:00.0000000-05:00");
        var replaced = (OrderedDictionary<string, object?>)ParityDump.ReplaceTimestamps(new OrderedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["timestamp"] = ParityDump.SpellTimestamp(whole.AddTicks(1)),
            ["last_seen"] = ParityDump.SpellTimestamp(whole.AddTicks(1_234_000)),
        })!;
        replaced["timestamp"].Should().Be("<invalid timestamp: 2026-09-13T10:00:00.0000001+00:00>");
        replaced["last_seen"].Should().Be("<timestamp>");
    }

    [Fact]
    public void RespellRoots_rewrites_each_root_once_longest_first()
    {
        var directory = Path.Combine(_tmp.FullName, "x", "locked");
        var file = directory + ".json";
        var response = new OrderedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["results"] = new List<object?>
            {
                new OrderedDictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["roots"] = new List<object?> { directory, file, "relative/root" },
                    ["message"] = $"Permission denied: {file}",
                },
            },
        };

        ParityDump.RespellRoots(response);

        var result = (OrderedDictionary<string, object?>)((List<object?>)response["results"]!)[0]!;
        var relativeFile = Path.GetRelativePath(Environment.CurrentDirectory, file);
        ((List<object?>)result["roots"]!).Should().Equal(Path.GetRelativePath(Environment.CurrentDirectory, directory), relativeFile, "relative/root");
        result["message"].Should().Be($"Permission denied: {relativeFile}");
    }
}
