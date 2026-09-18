using DriftBuster.Backend.Models;
using DriftBuster.Backend.MultiServer;
using DriftBuster.Backend.Settings;
using DriftBuster.Backend.Tests.Secrets;

namespace DriftBuster.Backend.Tests.Settings;

/// <summary>Files compared setting by setting against the baseline server, with the per-server summary people read first.</summary>
// Values are checked against the process-wide secret rules that the secret scanner tests replace.
[Collection(SecretRuleCacheCollection.Name)]
public sealed class SettingsComparisonBuilderTests
{
    private static readonly MultiServerPlan[] Plans =
    [
        new() { HostId = "a", Label = "baseline", Roots = ["/a"] },
        new() { HostId = "b", Label = "staging", Roots = ["/b"] },
        new() { HostId = "c", Label = "prod", Roots = ["/c"] },
    ];

    private static ConfigRecord Json(string path, string text) => new()
    {
        ConfigId = "json/" + path,
        DisplayName = path,
        FormatId = "json",
        ContentType = "json",
        Canonical = text,
        Raw = text,
        FileHash = "h",
        SourcePath = path,
        PluginName = "json",
        RelativePath = path,
    };

    private static OrderedDictionary<string, ConfigRecord> Host(params ConfigRecord[] records)
    {
        var host = new OrderedDictionary<string, ConfigRecord>(StringComparer.Ordinal);
        foreach (var record in records)
        {
            host[record.ConfigId] = record;
        }

        return host;
    }

    private static ServerScanResult Scanned(string hostId, ServerScanStatus status = ServerScanStatus.Succeeded) => new() { HostId = hostId, Status = status, Message = status.ToString() };

    private static SettingsComparison Compare(
        Dictionary<string, OrderedDictionary<string, ConfigRecord>> configs,
        Dictionary<string, IReadOnlyList<string>>? unreadable = null,
        ServerScanResult[]? results = null) =>
        SettingsComparisonBuilder.Build(Plans, configs, unreadable ?? new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal), results ?? [Scanned("a"), Scanned("b"), Scanned("c")], "a", TestContext.Current.CancellationToken);

    [Fact]
    public void ReorderedKeysMatchAndChangedValuesDiffer()
    {
        var comparison = Compare(new(StringComparer.Ordinal)
        {
            ["a"] = Host(Json("app.json", "{\"x\": 1, \"y\": 2}")),
            ["b"] = Host(Json("app.json", "{\"y\": 2, \"x\": 1}")),
            ["c"] = Host(Json("app.json", "{\"x\": 5, \"z\": 3}")),
        });

        var file = comparison.Files.Should().ContainSingle().Subject;
        file.Mode.Should().Be("settings");
        file.SettingsDiffering.Should().Be(3);
        file.Settings.Select(row => row.Key).Should().Equal("x", "y", "z");
        var x = file.Settings[0].Values;
        x.Select(value => value.DiffersFromBaseline).Should().Equal(false, false, true);
        file.Settings[1].Values[2].State.Should().Be(SettingValueState.NotSet);
        file.Settings[2].Values[0].State.Should().Be(SettingValueState.NotSet);

        comparison.Hosts[1].MatchesBaseline.Should().BeTrue();
        comparison.Hosts[2].SettingsDiffering.Should().Be(3);
        comparison.Hosts[2].FilesDiffering.Should().Be(1);
        comparison.Hosts[0].IsBaseline.Should().BeTrue();
    }

    [Fact]
    public void MissingExtraAndUnreadableFilesAreNamed()
    {
        var comparison = Compare(
            new(StringComparer.Ordinal)
            {
                ["a"] = Host(Json("legacy.json", "{}"), Json("locked.json", "{\"k\": 1}")),
                ["b"] = Host(Json("legacy.json", "{}"), Json("LOCKED.json", "{\"k\": 1}")),
                ["c"] = Host(Json("hotfix.json", "{\"h\": 1}")),
            },
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal) { ["c"] = ["locked.json"] });

        var prod = comparison.Hosts[2];
        prod.FilesMissing.Should().Equal("legacy.json");
        prod.FilesExtra.Should().Equal("hotfix.json");
        prod.FilesUnreadable.Should().Equal("locked.json");
        prod.MatchesBaseline.Should().BeFalse();
        comparison.Hosts[1].MatchesBaseline.Should().BeTrue("paths match case-insensitively");

        var hotfix = comparison.Files.Single(file => string.Equals(file.Path, "hotfix.json", StringComparison.Ordinal));
        hotfix.Presence.Select(value => value.State).Should().Equal(SettingValueState.FileMissing, SettingValueState.FileMissing, SettingValueState.Value);
        hotfix.Settings.Single().Values.Select(value => value.State).Should().Equal(SettingValueState.FileMissing, SettingValueState.FileMissing, SettingValueState.Value);
    }

    [Fact]
    public void SecretsAreComparedButNotShown()
    {
        var comparison = Compare(new(StringComparer.Ordinal)
        {
            ["a"] = Host(Json("s.json", "{\"db\": {\"password\": \"one\"}, \"note\": \"password=hunter2hunter2\"}")),
            ["b"] = Host(Json("s.json", "{\"db\": {\"password\": \"one\"}, \"note\": \"password=hunter2hunter2\"}")),
            ["c"] = Host(Json("s.json", "{\"db\": {\"password\": \"two\"}, \"note\": \"plain\"}")),
        });

        var password = comparison.Files.Single().Settings.Single(row => string.Equals(row.Key, "db.password", StringComparison.Ordinal));
        password.Values.Should().OnlyContain(value => value.Masked && value.Value == null);
        password.Values.Select(value => value.DiffersFromBaseline).Should().Equal(false, false, true);
        comparison.Files.Single().Settings.Single(row => string.Equals(row.Key, "note", StringComparison.Ordinal)).Values.Should().OnlyContain(value => value.Value == null);
    }

    [Fact]
    public void AServerThatWasNotScannedIsNotCountedAsDifferent()
    {
        var comparison = Compare(
            new(StringComparer.Ordinal) { ["a"] = Host(Json("app.json", "{\"x\": 1}")), ["b"] = Host(Json("app.json", "{\"x\": 1}")) },
            results: [Scanned("a"), Scanned("b"), Scanned("c", ServerScanStatus.Failed)]);

        var prod = comparison.Hosts[2];
        prod.Scanned.Should().BeFalse();
        prod.MatchesBaseline.Should().BeFalse();
        prod.FilesMissing.Should().BeEmpty();
        comparison.Files.Single().Settings.Single().Values[2].State.Should().Be(SettingValueState.NotScanned);
        comparison.Files.Single().Differs.Should().BeFalse();
    }

    [Fact]
    public void AFileTheBaselineLacksIsComparedWithTheFirstCopy()
    {
        var comparison = Compare(new(StringComparer.Ordinal)
        {
            ["a"] = Host(),
            ["b"] = Host(Json("only.json", "{\"x\": 1}")),
            ["c"] = Host(Json("only.json", "{\"x\": 2}")),
        });

        var row = comparison.Files.Single().Settings.Single();
        row.Values.Select(value => value.DiffersFromBaseline).Should().Equal(false, false, true);
        comparison.Hosts[1].FilesExtra.Should().Equal("only.json");
    }
}
