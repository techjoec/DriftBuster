using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Registry;

namespace DriftBuster.Backend.Tests.Registry;

/// <summary>
/// Mirror of the library half of tests/registry/test_registry_cli.py. Python runs <c>registry_cli.main(argv)</c> with
/// <c>is_windows</c>, <c>enumerate_installed_apps</c>, <c>find_app_registry_roots</c> and <c>search_registry</c> monkeypatched and
/// reads stdout; here the same fakes are swapped into the <see cref="RegistryCommands"/> seams and the command's lines are joined as
/// <c>print</c> writes them. <c>SystemExit</c> is <see cref="CommandExitException"/>; the return code of 0 and argv parsing belong to
/// the console tool.
/// </summary>
[Collection(RegistrySeamCollection.Name)]
public sealed class RegistryCliTests : IDisposable
{
    private readonly RegistrySeams _seams = new();

    public void Dispose() => _seams.Dispose();

    private static string Out(IEnumerable<string> lines) => string.Concat(lines.Select(line => line + "\n"));

    [Fact]
    public void RegistryCliRequiresWindows()
    {
        RegistryCommands.IsWindows = () => false;

        var act = () => RegistryCommands.ListApps();
        act.Should().Throw<CommandExitException>().Which.Message.Should().Contain("Windows");
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

        var output = Out(RegistryCommands.ListApps());
        output.Should().Contain("VendorA AppA").And.Contain("TinyTool");

        var called = new Dictionary<string, string>(StringComparer.Ordinal);
        RegistryCommands.FindAppRegistryRoots = (token, installed) =>
        {
            called["token"] = token;
            return [new RegistryRoot("HKLM", @"Software\\VendorA\\AppA", "64")];
        };
        output = Out(RegistryCommands.SuggestRoots("AppA"));
        called["token"].Should().Be("AppA");
        var normalized = output.Replace(@"\\", @"\", StringComparison.Ordinal);
        normalized.Should().Contain(@"HKLM \ Software\VendorA\AppA");
    }

    [Fact]
    public void RegistryCliSearch()
    {
        RegistryCommands.IsWindows = () => true;
        RegistryCommands.EnumerateInstalledApps = () => [new RegistryApp("VendorA AppA", @"Software\\...\\Uninstall\\AppA", "HKLM", View: "64")];
        RegistryCommands.FindAppRegistryRoots = (token, installed) => [new RegistryRoot("HKLM", @"Software\\VendorA\\AppA")];
        RegistryCommands.SearchRegistry = (roots, spec) =>
            [new RegistryHit(@"Software\\VendorA\\AppA", "HKLM", "Server", "api.internal.local", "keyword/pattern match")];

        var output = Out(RegistryCommands.Search(
            "VendorA",
            keywords: ["server"],
            patterns: [@"api\\.internal\\.local"],
            maxDepth: 3,
            maxHits: 5,
            timeBudget: 1.0));
        var normalized = output.Replace(@"\\", @"\", StringComparison.Ordinal);
        normalized.Should().Contain(@"HKLM \ Software\VendorA\AppA :: Server = api.internal.local");
    }

    /// <summary>argparse's <c>int</c> takes any size; <c>emit-config</c> prints it and <c>search</c> clamps it (<c>max(0, ...)</c>, <c>max(1, ...)</c>).</summary>
    [Fact]
    public void RegistryCliLimitsTakeIntegersOfAnySize()
    {
        RegistryCommands.IsWindows = () => true;
        RegistryCommands.EnumerateInstalledApps = () => [];
        RegistryCommands.FindAppRegistryRoots = (_, _) => [new RegistryRoot("HKLM", @"Software\V")];
        SearchSpec? seen = null;
        RegistryCommands.SearchRegistry = (_, spec) =>
        {
            seen = spec;
            return [];
        };
        var huge = System.Numerics.BigInteger.Parse("99999999999999999999", System.Globalization.CultureInfo.InvariantCulture);

        var snippet = RegistryCommands.EmitConfigJson(RegistryCommands.EmitConfig("T", maxDepth: huge, maxHits: -huge));
        snippet.Should().Contain("\"max_depth\": 99999999999999999999").And.Contain("\"max_hits\": -99999999999999999999");

        RegistryCommands.Search("V", maxDepth: huge, maxHits: -huge).Should().BeEmpty();
        seen!.MaxDepth.Should().Be(huge);
        seen.MaxHits.Should().Be(-huge);
    }
}
