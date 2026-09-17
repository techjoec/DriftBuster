using DriftBuster.Backend.Registry;

namespace DriftBuster.Backend.Tests.Registry;

/// <summary>
/// The registry usage summary over <see cref="RegistryOperations"/>, whose counters are process-wide.
/// </summary>
[Collection(RegistrySeamCollection.Name)]
public sealed class RegistrySummaryTests
{
    private const string UninstallPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";

    private sealed class RecordingBackend : IRegistryBackend
    {
        private readonly Dictionary<RegistryRoot, List<string>> _subkeys;
        private readonly Dictionary<RegistryRoot, List<KeyValuePair<string, object?>>> _values;

        public RecordingBackend()
        {
            var uninstallKey = $"{UninstallPath}\\ExampleApp";
            _subkeys = new()
            {
                [new RegistryRoot("HKLM", UninstallPath, "64")] = ["ExampleApp"],
                [new RegistryRoot("HKLM", uninstallKey, "64")] = [],
                [new RegistryRoot("HKCU", @"Software\ExampleApp")] = [],
            };
            _values = new()
            {
                [new RegistryRoot("HKLM", uninstallKey, "64")] =
                [
                    new("DisplayName", "ExampleApp"),
                    new("Publisher", "ExampleCorp"),
                    new("DisplayVersion", "1.2.3"),
                ],
                [new RegistryRoot("HKCU", @"Software\ExampleApp")] = [new("SettingName", "Example value"), new("Another", 42)],
            };
        }

        public IReadOnlyList<string> EnumSubkeys(string hive, string path, string? view)
            => _subkeys.GetValueOrDefault(new RegistryRoot(hive, path, view), []).ToList();

        public IReadOnlyList<KeyValuePair<string, object?>> EnumValues(string hive, string path, string? view)
            => _values.GetValueOrDefault(new RegistryRoot(hive, path, view), []).ToList();
    }

    private sealed class FailingBackend : IRegistryBackend
    {
        public IReadOnlyList<string> EnumSubkeys(string hive, string path, string? view) => throw new InvalidOperationException("backend not initialised");

        public IReadOnlyList<KeyValuePair<string, object?>> EnumValues(string hive, string path, string? view)
            => throw new InvalidOperationException("backend not initialised");
    }

    private static Dictionary<string, OrderedDictionary<string, object?>> Summary(bool reset = false)
        => RegistryOperations.RegistrySummary(reset).ToDictionary(entry => (string)entry["operation"]!, entry => entry, StringComparer.Ordinal);

    [Fact]
    public void RegistrySummaryTracksUsageStatistics()
    {
        RegistryOperations.RegistrySummary(reset: true);
        var backend = new RecordingBackend();

        var apps = RegistryOperations.EnumerateInstalledApps(backend: backend);
        apps.Should().HaveCount(1);

        var roots = RegistryOperations.FindAppRegistryRoots("ExampleApp", installed: apps);
        roots.Should().NotBeEmpty();

        var spec = new SearchSpec { Keywords = ["example"], MaxDepth = 0, MaxHits = 5 };
        var hits = RegistryOperations.SearchRegistry([roots[0]], spec, backend: backend);
        hits.Should().NotBeEmpty();

        var summary = Summary();

        var enumerateStats = summary["enumerate_installed_apps"];
        enumerateStats["calls"].Should().Be(1);
        enumerateStats["successes"].Should().Be(1);
        enumerateStats["errors"].Should().Be(0);
        ((double)enumerateStats["avg_duration_ms"]!).Should().BeGreaterThanOrEqualTo(0.0);
        enumerateStats["first_invocation"].Should().NotBeNull();
        enumerateStats["last_invocation"].Should().NotBeNull();

        var searchStats = summary["search_registry"];
        searchStats["calls"].Should().Be(1);
        searchStats["successes"].Should().Be(1);
        searchStats["errors"].Should().Be(0);
        searchStats["last_error"].Should().BeNull();
        ((double)searchStats["last_duration_ms"]!).Should().BeGreaterThanOrEqualTo(0.0);
    }

    [Fact]
    public void RegistrySummaryRecordsErrorsAndReset()
    {
        RegistryOperations.RegistrySummary(reset: true);

        var failingBackend = new FailingBackend();
        var act = () => RegistryOperations.SearchRegistry([new RegistryRoot("HKLM", @"Software\Broken")], new SearchSpec(), backend: failingBackend);
        act.Should().Throw<InvalidOperationException>();

        var stats = Summary()["search_registry"];
        stats["calls"].Should().Be(1);
        stats["successes"].Should().Be(0);
        stats["errors"].Should().Be(1);
        stats["last_error"].Should().BeOfType<string>().Which.Should().Be("RuntimeError: backend not initialised");

        RegistryOperations.RegistrySummary(reset: true);
        var resetSummary = Summary();
        resetSummary["search_registry"]["calls"].Should().Be(0);
        resetSummary["search_registry"]["errors"].Should().Be(0);
    }
}
