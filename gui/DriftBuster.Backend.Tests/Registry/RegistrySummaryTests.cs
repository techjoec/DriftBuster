using DriftBuster.Backend.Registry;

namespace DriftBuster.Backend.Tests.Registry;

/// <summary>
/// Mirror of tests/registry/test_registry_summary.py over <see cref="RegistryOperations"/>, whose counters are process-wide as
/// <c>_USAGE</c> is; <c>_RecordingBackend</c> and <c>_FailingBackend</c> are the nested backends below (Python's
/// <c>RuntimeError</c> is <see cref="InvalidOperationException"/>). Also the snapshot's key order and the timestamp format against
/// CPython's <c>_format_timestamp</c>.
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

    // search_registry coerces int(spec.max_depth) itself, so SearchSpec(max_depth=None) counts as a call and an error; the port's
    // typed spec is built inside the instrumented call through the Func<SearchSpec> overload.
    [Fact]
    public void ASpecWhoseLimitsDoNotConvertCountsACallAndAnError()
    {
        RegistryOperations.RegistrySummary(reset: true);
        var act = () => RegistryOperations.SearchRegistry(
            [new RegistryRoot("HKLM", @"Software\ExampleApp")],
            () => new SearchSpec { MaxDepth = Backend.Infrastructure.PythonBuiltins.Int(null) },
            new RecordingBackend());
        act.Should().Throw<Backend.Infrastructure.PythonTypeException>();

        var stats = Summary(reset: true)["search_registry"];
        stats["calls"].Should().Be(1);
        stats["successes"].Should().Be(0);
        stats["errors"].Should().Be(1);
        stats["last_error"].Should().Be("TypeError: int() argument must be a string, a bytes-like object or a real number, not 'NoneType'");
    }

    [Fact]
    public void SnapshotKeysOrderAndResetValues()
    {
        var summary = RegistryOperations.RegistrySummary(reset: true);
        summary.Select(entry => entry["operation"]).Should().Equal("enumerate_installed_apps", "find_app_registry_roots", "search_registry");

        var cleared = RegistryOperations.RegistrySummary();
        cleared[0].Keys.Should().Equal(
            "operation", "calls", "successes", "errors", "total_duration_ms", "avg_duration_ms", "last_duration_ms", "first_invocation",
            "last_invocation", "last_error");
        cleared[0]["total_duration_ms"].Should().Be(0.0);
        cleared[0]["avg_duration_ms"].Should().Be(0.0);
        cleared[0]["last_duration_ms"].Should().Be(0.0);
        cleared[0]["first_invocation"].Should().BeNull();
        cleared[0]["last_error"].Should().BeNull();
    }

    [Fact]
    public void SuccessAfterErrorClearsLastError()
    {
        RegistryOperations.RegistrySummary(reset: true);
        var act = () => RegistryOperations.EnumerateInstalledApps(new FailingBackend());
        act.Should().Throw<InvalidOperationException>();
        RegistryOperations.EnumerateInstalledApps(new RecordingBackend());

        var stats = Summary(reset: true)["enumerate_installed_apps"];
        stats["calls"].Should().Be(2);
        stats["errors"].Should().Be(1);
        stats["successes"].Should().Be(1);
        stats["last_error"].Should().BeNull();
        PythonDateTimeText(stats["first_invocation"]).Should().BeOnOrBefore(PythonDateTimeText(stats["last_invocation"]));
    }

    [Fact]
    public void FormatTimestampMatchesCPython()
    {
        RegistryOperations.FormatTimestamp(null).Should().BeNull();
        foreach (var entry in RegistryOracle.Items(RegistryOracle.Section("timestamps")).Select(RegistryOracle.Map))
        {
            var input = Convert.ToDouble(entry["input"], System.Globalization.CultureInfo.InvariantCulture);
            RegistryOperations.FormatTimestamp(input).Should().Be((string)entry["result"]!, $"input {input:R}");
        }
    }

    private static DateTimeOffset PythonDateTimeText(object? value)
        => DateTimeOffset.Parse((string)value!, System.Globalization.CultureInfo.InvariantCulture);
}
