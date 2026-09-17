using System.Globalization;
using System.Numerics;

using DriftBuster.Backend.Diff;
using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Infrastructure.PythonRe;
using DriftBuster.Backend.Registry;

namespace DriftBuster.Backend.Tests.Registry;

/// <summary>
/// The registry port against CPython 3.13 on <c>Data/registry_cases.json</c> (<c>tools/parity/gen_registry_cases.py</c>): root
/// descriptors, remote target arguments and payloads, scan sources with their destination names, value texts and matching, vendor
/// pairs, root suggestions, installed application enumeration, the breadth-first search, the CLI commands' stdout, errors and registry
/// calls, and the offline runner's registry branch. Values are compared as <c>json.dumps</c> text, so types and key order count.
/// </summary>
[Collection(RegistrySeamCollection.Name)]
public sealed class RegistryOracleTests : IDisposable
{
    private readonly RegistrySeams _seams = new();
    private readonly DirectoryInfo _tmp = Directory.CreateTempSubdirectory("driftbuster-registry-oracle-");

    public void Dispose()
    {
        _seams.Dispose();
        _tmp.Delete(recursive: true);
    }

    private static IEnumerable<OrderedDictionary<string, object?>> Cases(string section)
        => RegistryOracle.Items(RegistryOracle.Section(section)).Select(RegistryOracle.Map);

    private static string Json(object? value) => Canonicaliser.Dumps(value, indent: false, ensureAscii: true, sortKeys: false);

    // {"result": ...} or {"error": {"type", "message"}} as the generator's outcome() writes it.
    private static OrderedDictionary<string, object?> Outcome(Func<object?> action)
    {
        try
        {
            return new(StringComparer.Ordinal) { ["result"] = action() };
        }
        catch (Exception exc) when (exc is not OutOfMemoryException)
        {
            return new(StringComparer.Ordinal) { ["error"] = RegistryOracle.Error(exc) };
        }
    }

    private static void ShouldMatch(OrderedDictionary<string, object?> actual, OrderedDictionary<string, object?> expected, object? input)
    {
        var expectedOutcome = new OrderedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var key in new[] { "result", "error" }.Where(expected.ContainsKey))
        {
            expectedOutcome[key] = expected[key];
        }

        Json(actual).Should().Be(Json(expectedOutcome), $"input {Json(input)}");
    }

    private static List<object?> RootList(RegistryRoot root) => [root.Hive, root.Path, root.View];

    private static OrderedDictionary<string, object?> RootMap(RegistryRoot root) => new(StringComparer.Ordinal)
    {
        ["hive"] = root.Hive,
        ["path"] = root.Path,
        ["view"] = root.View,
    };

    private static object? Int(BigInteger? value) => value is { } number ? PythonValues.Narrow(number) : null;

    internal static OrderedDictionary<string, object?> TargetMap(RemoteRegistryTarget target) => new(StringComparer.Ordinal)
    {
        ["host"] = target.Host,
        ["transport"] = target.Transport,
        ["port"] = Int(target.Port),
        ["use_ssl"] = target.UseSsl,
        ["username"] = target.Username,
        ["password_env"] = target.PasswordEnv,
        ["credential_profile"] = target.CredentialProfile,
        ["alias"] = target.Alias,
    };

    private static OrderedDictionary<string, object?> SourceMap(OfflineRegistryScanSource source) => new(StringComparer.Ordinal)
    {
        ["token"] = source.Token,
        ["keywords"] = source.Keywords.Cast<object?>().ToList(),
        ["patterns"] = source.Patterns.Cast<object?>().ToList(),
        ["max_depth"] = Int(source.MaxDepth),
        ["max_hits"] = Int(source.MaxHits),
        ["time_budget_s"] = source.TimeBudgetS,
        ["alias"] = source.Alias,
        ["remote"] = source.Remote is null ? null : TargetMap(source.Remote),
        ["remote_batch"] = source.RemoteBatch.Select(target => (object?)TargetMap(target)).ToList(),
        ["roots"] = source.Roots.Select(root => (object?)RootMap(root)).ToList(),
    };

    private static OrderedDictionary<string, object?> AppMap(RegistryApp app) => new(StringComparer.Ordinal)
    {
        ["display_name"] = app.DisplayName,
        ["key_path"] = app.KeyPath,
        ["hive"] = app.Hive,
        ["publisher"] = app.Publisher,
        ["version"] = app.Version,
        ["uninstall_string"] = app.UninstallString,
        ["install_location"] = app.InstallLocation,
        ["view"] = app.View,
    };

    private static OrderedDictionary<string, object?> HitMap(RegistryHit hit) => new(StringComparer.Ordinal)
    {
        ["path"] = hit.Path,
        ["hive"] = hit.Hive,
        ["value_name"] = hit.ValueName,
        ["data_preview"] = hit.DataPreview,
        ["reason"] = hit.Reason,
    };

    [Fact]
    public void RootDescriptorsMatchCPython()
    {
        foreach (var entry in Cases("root_descriptors"))
        {
            var input = (string)entry["input"]!;
            ShouldMatch(Outcome(() => RootList(RegistryRoot.Parse(input))), entry, input);
        }
    }

    [Fact]
    public void PatternErrorsMatchCPython()
    {
        foreach (var entry in Cases("pattern_errors"))
        {
            var input = (string)entry["input"]!;
            ShouldMatch(Outcome(() => RegistryPython.Compile(input).Pattern), entry, input);
        }
    }

    [Fact]
    public void RemoteTargetArgsMatchCPython()
    {
        foreach (var entry in Cases("remote_target_args"))
        {
            var input = (string)entry["input"]!;
            ShouldMatch(Outcome(() => RegistryCommands.ParseRemoteTargetArg(input)), entry, input);
        }
    }

    [Fact]
    public void RemoteTargetsMatchCPython()
    {
        foreach (var entry in Cases("remote_targets"))
        {
            var input = entry["input"];
            ShouldMatch(Outcome(() => TargetMap(RemoteRegistryTarget.FromPayload(input))), entry, input);
        }
    }

    [Fact]
    public void ScanSourcesMatchCPython()
    {
        foreach (var entry in Cases("scan_sources"))
        {
            var input = RegistryOracle.Map(entry["input"]);
            ShouldMatch(Outcome(() => SourceMap(OfflineRegistryScanSource.FromDict(input))), entry, input);
            if (entry.TryGetValue("destination_name", out var expectedName))
            {
                OfflineRegistryScanSource.FromDict(input).DestinationName(fallbackIndex: 3).Should().Be((string)expectedName!);
            }
        }
    }

    [Fact]
    public void ValueTextsMatchCPython()
    {
        foreach (var entry in Cases("value_texts"))
        {
            var tree = new List<object?>
            {
                RemoteSchemaTests.Map(("hive", "HKLM"), ("path", "K"), ("view", "*"), ("values", new List<object?> { new List<object?> { "Name", entry["value"] } })),
            };
            var hits = RegistryScan.SearchRegistry([new RegistryRoot("HKLM", "K")], new SearchSpec(), new OracleRegistryBackend(tree));
            (hits.Count > 0 ? hits[0].DataPreview : null).Should().Be((string?)entry["preview"], $"value {Json(entry["value"])}");
        }
    }

    [Fact]
    public void MatchesMatchCPython()
    {
        foreach (var entry in Cases("matches"))
        {
            var tree = new List<object?>
            {
                RemoteSchemaTests.Map(("hive", "HKLM"), ("path", "K"), ("values", new List<object?> { new List<object?> { entry["name"], entry["value"] } })),
            };
            var spec = new SearchSpec
            {
                Keywords = RegistryOracle.Items(entry["keywords"]).Cast<string>().ToList(),
                Patterns = RegistryOracle.Items(entry["patterns"]).Cast<string>().Select(pattern => PythonPattern.Compile(pattern)).ToList(),
            };
            var hits = RegistryScan.SearchRegistry([new RegistryRoot("HKLM", "K")], spec, new OracleRegistryBackend(tree));
            (hits.Count > 0 ? hits[0].DataPreview : null).Should().Be((string?)entry["preview"], Json(entry));
        }
    }

    [Fact]
    public void VendorPairsMatchCPython()
    {
        foreach (var entry in Cases("vendor_pairs"))
        {
            var pairs = RegistryScan.CandidateVendorAppPairs((string)entry["input"]!).Select(pair => (object?)new List<object?> { pair.Vendor, pair.Product }).ToList();
            Json(pairs).Should().Be(Json(entry["result"]), Json(entry["input"]));
        }
    }

    [Fact]
    public void FindRootsMatchCPython()
    {
        foreach (var entry in Cases("find_roots"))
        {
            var installed = RegistryOracle.Items(entry["installed"]).Select(RegistryOracle.App).ToList();
            var roots = RegistryScan.FindAppRegistryRoots((string)entry["token"]!, installed).Select(root => (object?)RootList(root)).ToList();
            Json(roots).Should().Be(Json(entry["result"]), Json(entry["token"]));
        }
    }

    [Fact]
    public void EnumerateAppsMatchCPython()
    {
        var section = RegistryOracle.Map(RegistryOracle.Section("enumerate_apps"));
        var apps = RegistryScan.EnumerateInstalledApps(new OracleRegistryBackend(section["tree"]));
        Json(apps.Select(app => (object?)AppMap(app)).ToList()).Should().Be(Json(section["result"]));
    }

    [Fact]
    public void SearchMatchesCPython()
    {
        var section = RegistryOracle.Map(RegistryOracle.Section("search"));
        var backend = new OracleRegistryBackend(section["tree"]);
        foreach (var entry in RegistryOracle.Items(section["cases"]).Select(RegistryOracle.Map))
        {
            var spec = new SearchSpec
            {
                Keywords = RegistryOracle.Items(entry["keywords"]).Cast<string>().ToList(),
                Patterns = RegistryOracle.Items(entry["patterns"]).Cast<string>().Select(pattern => PythonPattern.Compile(pattern)).ToList(),
                MaxDepth = Convert.ToInt64(entry["max_depth"], CultureInfo.InvariantCulture),
                MaxHits = Convert.ToInt64(entry["max_hits"], CultureInfo.InvariantCulture),
            };
            var roots = RegistryOracle.Items(entry["roots"]).Select(RegistryOracle.Root).ToList();
            ShouldMatch(Outcome(() => RegistryScan.SearchRegistry(roots, spec, backend).Select(hit => (object?)HitMap(hit)).ToList()), entry, entry);
        }
    }

    [Fact]
    public void CliRunsMatchCPython()
    {
        var section = RegistryOracle.Map(RegistryOracle.Section("cli"));
        var apps = RegistryOracle.Items(section["apps"]).Select(RegistryOracle.App).ToList();
        var roots = RegistryOracle.Items(section["roots"]).Select(RegistryOracle.Root).ToList();
        var hits = RegistryOracle.Items(section["hits"]).Select(RegistryOracle.Hit).ToList();
        foreach (var run in RegistryOracle.Items(section["runs"]).Select(RegistryOracle.Map))
        {
            var argv = RegistryOracle.Items(run["argv"]).Cast<string>().ToList();
            var calls = new List<object?>();
            RegistryCommands.IsWindows = () => true;
            RegistryCommands.EnumerateInstalledApps = () =>
            {
                calls.Add(new List<object?> { "enumerate" });
                return apps;
            };
            RegistryCommands.FindAppRegistryRoots = (token, installed) =>
            {
                calls.Add(new List<object?> { "find", token });
                return roots;
            };
            RegistryCommands.SearchRegistry = (searchRoots, spec) =>
            {
                calls.Add(SearchCall(searchRoots, spec));
                return hits;
            };

            var stdout = new List<string>();
            var outcome = Outcome(() =>
            {
                stdout.AddRange(RegistryCliArgv.Run(argv));
                return 0;
            });
            ShouldMatch(outcome, run, run["argv"]);
            string.Concat(stdout.Select(line => line + "\n")).Should().Be((string)run["stdout"]!, Json(run["argv"]));
            Json(calls).Should().Be(Json(run["calls"]), Json(run["argv"]));
        }
    }

    private static List<object?> SearchCall(IReadOnlyList<RegistryRoot> roots, SearchSpec spec) =>
    [
        "search",
        roots.Select(root => (object?)RootList(root)).ToList(),
        spec.Keywords.Cast<object?>().ToList(),
        spec.Patterns.Select(pattern => (object?)pattern.Pattern).ToList(),
        PythonValues.Narrow(spec.MaxDepth),
        PythonValues.Narrow(spec.MaxHits),
        spec.TimeBudgetS,
    ];

    [Fact]
    public void CollectorMatchesCPython()
    {
        var cli = RegistryOracle.Map(RegistryOracle.Section("cli"));
        var apps = RegistryOracle.Items(cli["apps"]).Select(RegistryOracle.App).ToList();
        var roots = RegistryOracle.Items(cli["roots"]).Select(RegistryOracle.Root).ToList();
        var hits = RegistryOracle.Items(cli["hits"]).Select(RegistryOracle.Hit).ToList();
        var index = 0;
        foreach (var entry in Cases("collector"))
        {
            var calls = new List<object?>();
            RegistryScanCollector.IsWindows = () => true;
            RegistryScanCollector.EnumerateInstalledApps = () =>
            {
                calls.Add(new List<object?> { "enumerate" });
                return apps;
            };
            RegistryScanCollector.FindAppRegistryRoots = (token, installed) =>
            {
                calls.Add(new List<object?> { "find", token, installed.Count });
                return roots;
            };
            RegistryScanCollector.SearchRegistry = (searchRoots, spec) =>
            {
                calls.Add(SearchCall(searchRoots, spec));
                return hits;
            };

            var source = OfflineRegistryScanSource.FromDict(RegistryOracle.Map(entry["input"]));
            var destination = Directory.CreateDirectory(Path.Combine(_tmp.FullName, $"case{index++}", source.DestinationName(0))).FullName;
            var logs = new List<string>();
            var result = RegistryScanCollector.Collect(source, destination, logs.Add);

            Json(calls).Should().Be(Json(entry["calls"]));
            logs.Should().Equal($"registry scan started for token: {source.Token}");
            var expectedSummary = RegistryOracle.Map(entry["summary"]);
            var keys = new[] { "type", "token", "keywords", "patterns", "roots", "hits", "output", "requested_roots" }.Where(expectedSummary.ContainsKey);
            result.Summary.Keys.Should().Equal(keys);
            foreach (var key in keys.Where(key => !string.Equals(key, "output", StringComparison.Ordinal)))
            {
                Json(result.Summary[key]).Should().Be(Json(expectedSummary[key]), key);
            }

            var written = PythonPurePath.Join(destination, "registry_scan.json");
            result.Summary["output"].Should().Be(OperatingSystem.IsWindows() ? written.Replace('\\', '/') : written);
            var text = File.ReadAllText(result.ResultPath!).ReplaceLineEndings("\n");
            text.Should().Be((string)entry["payload_text"]!);
            if (!OperatingSystem.IsWindows())
            {
                result.Size.Should().Be(Convert.ToInt64(entry["size"], CultureInfo.InvariantCulture));
                result.Sha256.Should().Be((string)entry["sha256"]!);
            }
        }
    }

    [Fact]
    public void CollectorSkipsOffWindows()
    {
        RegistryScanCollector.IsWindows = () => false;
        RegistryScanCollector.SearchRegistry = (_, _) => throw new InvalidOperationException("search must not run");
        var source = OfflineRegistryScanSource.FromDict(RemoteSchemaTests.Map(
            ("registry_scan", RemoteSchemaTests.Map(("token", "T"), ("keywords", "a b"), ("patterns", new List<object?> { "p" })))));
        var logs = new List<string>();

        var result = RegistryScanCollector.Collect(source, _tmp.FullName, logs.Add);

        logs.Should().Equal("registry scan skipped: non-Windows platform");
        Json(result.Summary).Should().Be("""{"type": "registry_scan", "token": "T", "keywords": ["a", "b"], "patterns": ["p"], "skipped": true, "reason": "not-windows"}""");
        result.ResultPath.Should().BeNull();
        File.Exists(Path.Combine(_tmp.FullName, "registry_scan.json")).Should().BeFalse();
    }
}
