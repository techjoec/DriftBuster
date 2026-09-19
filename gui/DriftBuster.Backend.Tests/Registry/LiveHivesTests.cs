using System.Text;

using DriftBuster.Backend.Diff;
using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Registry;
using DriftBuster.Backend.Remote;

namespace DriftBuster.Backend.Tests.Registry;

/// <summary>
/// Live hive scans through the library: root descriptors, <see cref="RegistryCommands.EmitConfig"/> with explicit roots, and the
/// summary a capture manifest keeps of a registry scan file.
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
    public void A_registry_scan_file_summarises_its_roots_and_hits()
    {
        var registryJson = Path.Combine(_tmp.FullName, "registry_scan.json");
        File.WriteAllText(registryJson, """
            {"token": "VendorA", "roots": [{"hive": "HKLM", "path": "Software\\VendorA"}, {"hive": "", "path": "x"}],
             "requested_roots": [{"hive": "HKLM", "path": "Software\\VendorA", "view": "64"}],
             "hits": [{"hive": "HKLM", "path": "Software\\VendorA", "value_name": "Server"}]}
            """);

        var summary = CaptureRunner.SummariseRegistryScan(registryJson);

        summary.Should().BeEquivalentTo(new RegistryScanSummary("registry_scan.json", registryJson, "VendorA", [@"HKLM \ Software\VendorA"], [@"HKLM \ Software\VendorA (view 64)"], 1));
    }

    private static List<object?> Items(object? value) => (List<object?>)value!;

    private static OrderedDictionary<string, object?> Map(object? value) => (OrderedDictionary<string, object?>)value!;
}
