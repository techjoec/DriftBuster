using System.Text;

using DriftBuster.Backend.Diff;
using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Registry;
using DriftBuster.Backend.Remote;

namespace DriftBuster.Backend.Tests.Registry;

/// <summary>
/// Live hive scans through the library: root descriptors, <see cref="RegistryCommands.EmitConfig"/> with explicit roots, the offline
/// collector's registry branch (<see cref="RegistryScanCollector"/>) with its seams swapped, and registry scans embedded in a capture
/// manifest.
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

        EngineJson.TryLoads(RegistryCommands.EmitConfigJson(snippet), out var payload).Should().BeTrue();
        var roots = Map(Map(payload)["registry_scan"]).GetValueOrDefault("roots");
        var entry = Map(Items(roots).Should().ContainSingle().Subject);
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
        var normalisedRoots = Items(summary["roots"]).Cast<string>().Select(entry => entry.Replace(@"\\", @"\", StringComparison.Ordinal));
        normalisedRoots.Should().Equal(@"HKLM \ Software\VendorA");
        var normalisedRequested = Items(summary["requested_roots"]).Cast<string>().Select(entry => entry.Replace(@"\\", @"\", StringComparison.Ordinal));
        normalisedRequested.Should().Equal(@"HKLM \ Software\VendorA (view 64)");

        result.ResultPath.Should().NotBeNull();
        EngineJson.TryLoads(File.ReadAllText(Path.Combine(destination, "registry_scan.json"), Encoding.UTF8), out var payload).Should().BeTrue();
        var requested = Items(Map(payload)["requested_roots"]);
        Map(requested[0])["view"].Should().Be("64");
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
        var normalisedRoots = Items(summaries[0]["roots"]).Cast<string>().Select(entry => entry.Replace(@"\\", @"\", StringComparison.Ordinal));
        var normalisedRequested = Items(summaries[0]["requested_roots"]).Cast<string>().Select(entry => entry.Replace(@"\\", @"\", StringComparison.Ordinal));
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
        Map(manifest["counts"])["registry_scans"].Should().Be(1L);
        Map(Items(manifest["registry_scans"])[0])["file"].Should().Be(Path.GetFileName(registryJson));
    }

    private static List<object?> Items(object? value) => (List<object?>)value!;

    private static OrderedDictionary<string, object?> Map(object? value) => (OrderedDictionary<string, object?>)value!;
}
