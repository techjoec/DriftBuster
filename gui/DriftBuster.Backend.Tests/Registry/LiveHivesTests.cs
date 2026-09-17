using System.Text;

using DriftBuster.Backend.Diff;
using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Registry;
using DriftBuster.Backend.Remote;

namespace DriftBuster.Backend.Tests.Registry;

/// <summary>
/// Mirror of the library half of tests/registry/test_live_hives.py. <c>test_registry_cli_emit_config_with_roots</c> runs
/// <see cref="RegistryCommands.EmitConfig"/> and decodes <see cref="RegistryCommands.EmitConfigJson"/> as the test decodes stdout.
/// <c>test_offline_runner_uses_explicit_roots</c> runs the offline runner's registry branch, <see cref="RegistryScanCollector"/>,
/// with the package attributes it patches swapped into the collector's seams; the source comes from the same profile payload, and
/// the manifest source summary and the <c>registry_scan.json</c> payload are the ones the branch returns and writes (the offline
/// runner's staging and manifest are not ported). <c>test_capture_manifest_embeds_registry_scans</c> runs the capture port's
/// <see cref="CaptureRunner.LoadRegistryScanSummaries"/> and <see cref="CaptureRunner.BuildManifestPayload"/>.
/// </summary>
[Collection(RegistrySeamCollection.Name)]
public sealed class LiveHivesTests : IDisposable
{
    private readonly RegistrySeams _seams = new();
    private readonly DirectoryInfo _tmp = Directory.CreateTempSubdirectory("driftbuster-live-hives-");

    public void Dispose()
    {
        _seams.Dispose();
        _tmp.Delete(recursive: true);
    }

    [Fact]
    public void ParseRegistryRootDescriptorVariants()
    {
        var root = RegistryRoot.Parse("HKLM\\Software\\Vendor,view=32");
        root.Hive.Should().Be("HKLM");
        root.Path.Should().Be("Software\\Vendor");
        root.View.Should().Be("32");

        var auto = RegistryRoot.Parse("HKCU\\Software\\Tool,view=auto");
        auto.Hive.Should().Be("HKCU");
        auto.View.Should().BeNull();
    }

    [Fact]
    public void RegistryCliEmitConfigWithRoots()
    {
        RegistryCommands.IsWindows = () => true;
        RegistryCommands.EnumerateInstalledApps = () => [];
        RegistryCommands.FindAppRegistryRoots = (token, installed) => [];

        var snippet = RegistryCommands.EmitConfig("VendorA", keywords: ["server"], roots: ["HKLM\\Software\\VendorA,view=64"]);

        PythonJson.TryLoads(RegistryCommands.EmitConfigJson(snippet), out var payload).Should().BeTrue();
        var roots = RegistryOracle.Map(RegistryOracle.Map(payload)["registry_scan"]).GetValueOrDefault("roots");
        var entry = RegistryOracle.Map(RegistryOracle.Items(roots).Should().ContainSingle().Subject);
        entry.ToList().Should().Equal(
            new KeyValuePair<string, object?>("hive", "HKLM"),
            new KeyValuePair<string, object?>("path", "Software\\VendorA"),
            new KeyValuePair<string, object?>("view", "64"));
    }

    [Fact]
    public void OfflineRunnerUsesExplicitRoots()
    {
        var sourcePayload = RemoteSchemaTests.Map(
            ("registry_scan", RemoteSchemaTests.Map(
                ("token", "VendorA"),
                ("roots", new List<object?> { RemoteSchemaTests.Map(("hive", "HKLM"), ("path", @"Software\\VendorA"), ("view", "64")) }))));
        var source = OfflineRegistryScanSource.FromDict(sourcePayload);

        var recorded = new Dictionary<string, object?>(StringComparer.Ordinal);
        RegistryScanCollector.IsWindows = () => true;
        RegistryScanCollector.FindAppRegistryRoots = (_, _) => throw new InvalidOperationException("find_app_registry_roots should not run when roots are supplied");
        RegistryScanCollector.EnumerateInstalledApps = () => throw new InvalidOperationException("find_app_registry_roots should not run when roots are supplied");
        RegistryScanCollector.SearchRegistry = (roots, spec) =>
        {
            recorded["roots"] = roots;
            recorded["keywords"] = spec.Keywords;
            return [new RegistryHit(@"Software\\VendorA", "HKLM", "Server", "api.internal", "keyword")];
        };

        var alias = source.DestinationName(fallbackIndex: 1);
        var destination = Directory.CreateDirectory(Path.Combine(_tmp.FullName, "data", alias)).FullName;
        var result = RegistryScanCollector.Collect(source, destination);

        recorded.Should().ContainKey("roots");
        ((IReadOnlyList<RegistryRoot>)recorded["roots"]!).Should().Equal(new RegistryRoot("HKLM", @"Software\\VendorA", "64"));

        var summary = result.Summary;
        summary["type"].Should().Be("registry_scan");
        var normalisedRoots = RegistryOracle.Items(summary["roots"]).Cast<string>().Select(entry => entry.Replace(@"\\", @"\", StringComparison.Ordinal));
        normalisedRoots.Should().Equal(@"HKLM \ Software\VendorA");
        var normalisedRequested = RegistryOracle.Items(summary["requested_roots"]).Cast<string>().Select(entry => entry.Replace(@"\\", @"\", StringComparison.Ordinal));
        normalisedRequested.Should().Equal(@"HKLM \ Software\VendorA (view 64)");

        result.ResultPath.Should().NotBeNull();
        PythonJson.TryLoads(File.ReadAllText(Path.Combine(destination, "registry_scan.json"), Encoding.UTF8), out var payload).Should().BeTrue();
        var requested = RegistryOracle.Items(RegistryOracle.Map(payload)["requested_roots"]);
        RegistryOracle.Map(requested[0])["view"].Should().Be("64");
    }

    [Fact]
    public void CaptureManifestEmbedsRegistryScans()
    {
        var registryJson = Path.Combine(_tmp.FullName, "registry_scan.json");
        var hit = RemoteSchemaTests.Map(("hive", "HKLM"), ("path", @"Software\\VendorA"), ("value_name", "Server"), ("data_preview", "api"), ("reason", "keyword"));
        var scan = RemoteSchemaTests.Map(
            ("token", "VendorA"),
            ("roots", new List<object?> { RemoteSchemaTests.Map(("hive", "HKLM"), ("path", @"Software\\VendorA")) }),
            ("requested_roots", new List<object?> { RemoteSchemaTests.Map(("hive", "HKLM"), ("path", @"Software\\VendorA"), ("view", "64")) }),
            ("hits", new List<object?> { hit }));
        File.WriteAllText(registryJson, Canonicaliser.Dumps(scan, indent: false, ensureAscii: true, sortKeys: false), new UTF8Encoding(false));

        var summaries = CaptureRunner.LoadRegistryScanSummaries([registryJson]);
        var normalisedRoots = RegistryOracle.Items(summaries[0]["roots"]).Cast<string>().Select(entry => entry.Replace(@"\\", @"\", StringComparison.Ordinal));
        var normalisedRequested = RegistryOracle.Items(summaries[0]["requested_roots"]).Cast<string>().Select(entry => entry.Replace(@"\\", @"\", StringComparison.Ordinal));
        normalisedRoots.Should().Equal(@"HKLM \ Software\VendorA");
        normalisedRequested.Should().Equal(@"HKLM \ Software\VendorA (view 64)");
        var manifest = CaptureRunner.BuildManifestPayload(
            capture: RemoteSchemaTests.Map(
                ("id", "capture"),
                ("captured_at", "2025-03-12T00:00:00Z"),
                ("root", _tmp.FullName),
                ("operator", "tester"),
                ("environment", "lab"),
                ("reason", "validation"),
                ("host", "test-host")),
            snapshotPath: Path.Combine(_tmp.FullName, "snapshot.json"),
            manifestPath: Path.Combine(_tmp.FullName, "manifest.json"),
            detectionDuration: 1.0,
            huntDuration: 0.5,
            totalDuration: 1.5,
            detectionCount: 0,
            profileMatchCount: 0,
            huntCount: 0,
            profileSummary: RemoteSchemaTests.Map(),
            placeholder: "[REDACTED]",
            maskTokenCount: 0,
            totalRedactions: 0,
            registryScans: summaries);
        RegistryOracle.Map(manifest["counts"])["registry_scans"].Should().Be(1L);
        RegistryOracle.Map(RegistryOracle.Items(manifest["registry_scans"])[0])["file"].Should().Be(Path.GetFileName(registryJson));
    }
}
