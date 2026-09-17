using DriftBuster.Backend.Registry;

namespace DriftBuster.Cli.Tests;

/// <summary>
/// Mirror of tests/registry/test_registry_cli.py through <c>driftbuster registry-scan</c> (<c>registry_cli.main(argv)</c>), with the
/// Windows gate and registry calls swapped in the <see cref="RegistryCommands"/> seams as Python monkeypatches the module. The seams are
/// process-wide, so the class runs outside the parallel tests and restores them after each test.
/// </summary>
[Collection(WorkingDirectoryCollection.Name)]
public sealed class RegistryCliTests : IDisposable
{
    private readonly Func<bool> _isWindows = RegistryCommands.IsWindows;
    private readonly Func<IReadOnlyList<RegistryApp>> _enumerate = RegistryCommands.EnumerateInstalledApps;
    private readonly Func<string, IReadOnlyList<RegistryApp>, IReadOnlyList<RegistryRoot>> _find = RegistryCommands.FindAppRegistryRoots;
    private readonly Func<IReadOnlyList<RegistryRoot>, SearchSpec, IReadOnlyList<RegistryHit>> _search = RegistryCommands.SearchRegistry;

    public void Dispose()
    {
        RegistryCommands.IsWindows = _isWindows;
        RegistryCommands.EnumerateInstalledApps = _enumerate;
        RegistryCommands.FindAppRegistryRoots = _find;
        RegistryCommands.SearchRegistry = _search;
    }

    [Fact]
    public void RegistryCliRequiresWindows()
    {
        RegistryCommands.IsWindows = () => false;

        var run = CliInvocation.Invoke("registry-scan", "list-apps");

        run.ExitCode.Should().Be(1);
        run.Err.Should().Contain("Windows");
    }

    [Fact]
    public void RegistryCliListAndSuggest()
    {
        RegistryCommands.IsWindows = () => true;
        RegistryApp[] fakeApps =
        [
            new("VendorA AppA", @"Software\\...\\Uninstall\\AppA", "HKLM", Version: "1.2.3", View: "64"),
            new("TinyTool", @"Software\\...\\Uninstall\\UserApp", "HKCU", Version: null, View: "auto"),
        ];
        RegistryCommands.EnumerateInstalledApps = () => fakeApps;

        var listed = CliInvocation.Invoke("registry-scan", "list-apps");
        listed.ExitCode.Should().Be(0, listed.Err);
        listed.Out.Should().Contain("VendorA AppA").And.Contain("TinyTool");

        var called = new Dictionary<string, string>(StringComparer.Ordinal);
        RegistryCommands.FindAppRegistryRoots = (token, installed) =>
        {
            called["token"] = token;
            return [new RegistryRoot("HKLM", @"Software\\VendorA\\AppA", "64")];
        };
        var suggested = CliInvocation.Invoke("registry-scan", "suggest-roots", "AppA");
        suggested.ExitCode.Should().Be(0, suggested.Err);
        called["token"].Should().Be("AppA");
        suggested.Out.Replace(@"\\", @"\", StringComparison.Ordinal).Should().Contain(@"HKLM \ Software\VendorA\AppA");
    }

    [Fact]
    public void RegistryCliSearch()
    {
        RegistryCommands.IsWindows = () => true;
        RegistryCommands.EnumerateInstalledApps = () => [new RegistryApp("VendorA AppA", @"Software\\...\\Uninstall\\AppA", "HKLM", View: "64")];
        RegistryCommands.FindAppRegistryRoots = (token, installed) => [new RegistryRoot("HKLM", @"Software\\VendorA\\AppA")];
        SearchSpec? seen = null;
        RegistryCommands.SearchRegistry = (roots, spec) =>
        {
            seen = spec;
            return [new RegistryHit(@"Software\\VendorA\\AppA", "HKLM", "Server", "api.internal.local", "keyword/pattern match")];
        };

        var run = CliInvocation.Invoke(
            "registry-scan", "search", "VendorA", "--keyword", "server", "--pattern", @"api\\.internal\\.local", "--max-depth", "3", "--max-hits", "5",
            "--time-budget", "1.0");

        run.ExitCode.Should().Be(0, run.Err);
        run.Out.Replace(@"\\", @"\", StringComparison.Ordinal).Should().Contain(@"HKLM \ Software\VendorA\AppA :: Server = api.internal.local");
        seen!.Keywords.Should().Equal("server");
        ((int)seen.MaxDepth).Should().Be(3);
        ((int)seen.MaxHits).Should().Be(5);
        seen.TimeBudgetS.Should().Be(1.0);
    }

    /// <summary><c>emit-config</c> prints <c>json.dumps(snippet, indent=2, sort_keys=True)</c>; a refused <c>--remote-target</c> escapes as <c>ValueError</c>.</summary>
    [Fact]
    public void RegistryCliEmitConfig()
    {
        RegistryCommands.IsWindows = () => true;
        RegistryCommands.EnumerateInstalledApps = () => [];

        var run = CliInvocation.Invoke(
            "registry-scan", "emit-config", "VendorA", "--alias", "vendor", "--keyword", "server", "--root", @"HKLM\Software\Vendor,view=64",
            "--remote-target", "host1,port=5986,use-ssl=yes", "--remote-target", "host2");

        run.ExitCode.Should().Be(0, run.Err);
        var snippet = run.Json();
        snippet.GetProperty("alias").GetString().Should().Be("vendor");
        var scan = snippet.GetProperty("registry_scan");
        scan.GetProperty("max_depth").GetInt32().Should().Be(12);
        scan.GetProperty("time_budget_s").GetRawText().Should().Be("10.0");
        scan.GetProperty("remote").GetProperty("port").GetInt32().Should().Be(5986);
        scan.GetProperty("remote_batch")[0].GetProperty("host").GetString().Should().Be("host2");
        scan.TryGetProperty("patterns", out _).Should().BeFalse();

        var refused = CliInvocation.Invoke("registry-scan", "emit-config", "VendorA", "--remote-target", "host,bogus=1");
        refused.ExitCode.Should().Be(1);
        refused.Err.Should().Be("ValueError: Unsupported remote target key 'bogus'" + Environment.NewLine);
    }

    /// <summary><c>type=int</c> refuses a value <c>int()</c> does not accept: a parse error with exit code 2.</summary>
    [Fact]
    public void RegistryCliRefusesAnInvalidIntegerAsAParseError()
    {
        RegistryCommands.IsWindows = () => true;

        var run = CliInvocation.Invoke("registry-scan", "search", "VendorA", "--max-depth", "deep");

        run.ExitCode.Should().Be(2);
        run.Err.Should().Contain("argument --max-depth: invalid int value: 'deep'");
    }
}
