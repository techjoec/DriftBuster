using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Registry;

namespace DriftBuster.Backend.Tests.Registry;

/// <summary>
/// <see cref="RegistryScan"/> over <see cref="FakeRegistryBackend"/>, with <see cref="RegistryScan.IsWindowsProbe"/> swapped and
/// restored after the test.
/// </summary>
[Collection(RegistrySeamCollection.Name)]
public sealed class RegistryScanTests : IDisposable
{
    private const string BaseHklm64 = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string BaseHklm32 = @"Software\Wow6432Node\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string BaseHkcu = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";

    private readonly RegistrySeams _seams = new();

    public void Dispose() => _seams.Dispose();

    internal static FakeRegistryBackend BuildFakeRegistry()
    {
        var fb = new FakeRegistryBackend();
        fb.AddKey("HKLM", BaseHklm64);
        fb.AddKey(
            "HKLM",
            BaseHklm64 + @"\AppA",
            ("DisplayName", "VendorA AppA"),
            ("Publisher", "VendorA"),
            ("DisplayVersion", "1.2.3"),
            ("InstallLocation", @"C:\\Program Files\\VendorA\\AppA"));

        fb.AddKey("HKLM", BaseHklm32);
        fb.AddKey("HKLM", BaseHklm32 + @"\AppB", ("DisplayName", "VendorB AppB"), ("Publisher", "VendorB"), ("DisplayVersion", "4.5.6"));

        fb.AddKey("HKCU", BaseHkcu);
        fb.AddKey("HKCU", BaseHkcu + @"\UserApp", ("DisplayName", "TinyTool"), ("DisplayVersion", "0.9"));

        fb.AddKey("HKLM", @"Software\VendorA\AppA", ("ConfigPath", @"C:\\data\\a.cfg"));
        fb.AddKey("HKLM", @"Software\VendorA\AppA\Settings", ("Server", "api.internal.local"));
        fb.AddKey("HKLM", @"Software\Wow6432Node\VendorB\AppB", ("Endpoint", "https://svc.corp.local"));
        fb.AddKey("HKCU", @"Software\TinyTool", ("Token", "abcd1234"));
        return fb;
    }

    [Fact]
    public void EnumerateInstalledAppsCollectsFromMultipleHives()
    {
        var fb = BuildFakeRegistry();
        var apps = RegistryScan.EnumerateInstalledApps(backend: fb);
        var names = apps.Select(a => a.DisplayName).ToList();
        names.Should().Contain("VendorA AppA");
        names.Should().Contain("VendorB AppB");
        names.Should().Contain("TinyTool");
    }

    [Fact]
    public void FindAppRegistryRootsUsesInstalledList()
    {
        var fb = BuildFakeRegistry();
        var apps = RegistryScan.EnumerateInstalledApps(backend: fb);
        var roots = RegistryScan.FindAppRegistryRoots("AppA", installed: apps);
        roots.Should().Contain(r => r.Hive == "HKLM" && r.Path.StartsWith(@"Software\VendorA\AppA", StringComparison.Ordinal));
        roots.Should().Contain(r => r.Path.StartsWith(@"Software\Microsoft\Windows\CurrentVersion\Uninstall", StringComparison.Ordinal));
    }

    [Fact]
    public void SearchRegistryMatchesKeywordsAndPatterns()
    {
        var fb = BuildFakeRegistry();
        RegistryRoot[] roots =
        [
            new("HKLM", @"Software\VendorA\AppA"),
            new("HKLM", @"Software\Wow6432Node\VendorB", "32"),
            new("HKCU", @"Software\TinyTool"),
        ];
        var spec = new SearchSpec { Keywords = ["server", "api"], Patterns = [PatternRegex.Create(@"api\.internal\.local")] };
        var hits = RegistryScan.SearchRegistry(roots, spec, backend: fb);
        hits.Should().Contain(h => h.ValueName == "Server");

        var spec2 = new SearchSpec { Patterns = [PatternRegex.Create("https://")] };
        var hits2 = RegistryScan.SearchRegistry(roots, spec2, backend: fb);
        hits2.Should().Contain(h => h.ValueName == "Endpoint");
    }

    [Fact]
    public void SearchRegistryDepthLimit()
    {
        var fb = BuildFakeRegistry();
        fb.AddKey("HKLM", @"Software\VendorA\Deep");
        const string Base = @"Software\VendorA\Deep";
        var last = Base;
        for (var i = 0; i < 5; i++)
        {
            var nextKey = last + $@"\K{i}";
            fb.AddKey("HKLM", nextKey);
            last = nextKey;
        }

        fb.AddKey("HKLM", last, ("Flag", "on"));

        RegistryRoot[] roots = [new("HKLM", Base)];
        var spec = new SearchSpec { Patterns = [PatternRegex.Create("on")], MaxDepth = 2 };
        var hits = RegistryScan.SearchRegistry(roots, spec, backend: fb);
        hits.Should().BeEmpty();
    }

    [Fact]
    public void SearchRegistryLimitsAndNameMatches()
    {
        var fb = BuildFakeRegistry();
        RegistryRoot[] roots = [new("HKLM", @"Software\VendorA\AppA")];
        fb.AddKey("HKLM", @"Software\VendorA\AppA\Many");
        for (var i = 0; i < 10; i++)
        {
            fb.AddKey("HKLM", @"Software\VendorA\AppA\Many", ($"Key{i}", $"value-{i}"));
        }

        var spec = new SearchSpec { Patterns = [PatternRegex.Create(@"value-\d+")], MaxHits = 3 };
        var hits = RegistryScan.SearchRegistry(roots, spec, backend: fb);
        hits.Should().HaveCount(3);

        fb.AddKey("HKLM", @"Software\VendorA\AppA\Names", ("Server", "something"));
        var spec2 = new SearchSpec { Keywords = ["server"] };
        var hits2 = RegistryScan.SearchRegistry(roots, spec2, backend: fb);
        hits2.Should().Contain(h => h.Path.EndsWith("Names", StringComparison.Ordinal) && h.ValueName == "Server");
    }

    [Fact]
    public void FindAppRegistryRootsFallbackWhenNoInstalledMatch()
    {
        var roots = RegistryScan.FindAppRegistryRoots("Acme Product", installed: []);
        roots.Should().Contain(r => r.Hive == "HKCU" && r.Path.StartsWith(@"Software\Acme Product", StringComparison.Ordinal));
        roots.Should().Contain(r => r.Hive == "HKLM" && r.Path.StartsWith(@"Software\Wow6432Node\Acme Product", StringComparison.Ordinal));
    }

    [Fact]
    public void DefaultBackendGuard()
    {
        RegistryScan.IsWindowsProbe = () => false;
        var act = () => RegistryScan.EnumerateInstalledApps(backend: null);
        act.Should().Throw<PlatformNotSupportedException>().WithMessage("Windows Registry scanning requires Windows platform");
    }
}
