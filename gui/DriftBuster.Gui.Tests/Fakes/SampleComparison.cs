using DriftBuster.Backend.Models;

namespace DriftBuster.Gui.Tests.Fakes;

/// <summary>A three-server settings comparison: staging matches, prod differs in one setting, lacks one file and has a masked secret.</summary>
internal static class SampleComparison
{
    public static SettingsComparison Build() => new()
    {
        BaselineHostId = "a",
        Hosts =
        [
            new() { HostId = "a", Label = "baseline", IsBaseline = true, Scanned = true, MatchesBaseline = true },
            new() { HostId = "b", Label = "staging", Scanned = true, MatchesBaseline = true },
            new() { HostId = "c", Label = "prod", Scanned = true, SettingsDiffering = 2, FilesDiffering = 2, FilesMissing = ["legacy.ini"] },
        ],
        Files =
        [
            new()
            {
                ConfigId = "json/app",
                Path = "inetpub/app/appsettings.json",
                Format = "json",
                Mode = "settings",
                SettingsDiffering = 2,
                Differs = true,
                Presence = [Present("a"), Present("b"), Present("c")],
                Settings =
                [
                    Row("Cache.Minutes", "15", "15", "60"),
                    Row("Logging.Default", "Info", "Info", "Info"),
                    new() { Key = "db.password", Differs = true, Values = [Masked("a", false), Masked("b", false), Masked("c", true)] },
                ],
            },
            new()
            {
                ConfigId = "ini/legacy",
                Path = "legacy.ini",
                Format = "ini",
                Mode = "settings",
                Differs = true,
                Presence = [Present("a"), Present("b"), new() { HostId = "c", State = SettingValueState.FileMissing, DiffersFromBaseline = true }],
                Settings = [new() { Key = "[a] b", Values = [Value("a", "1", false), Value("b", "1", false), new() { HostId = "c", State = SettingValueState.FileMissing }] }],
            },
            new()
            {
                ConfigId = "json/same",
                Path = "same.json",
                Format = "json",
                Mode = "settings",
                Presence = [Present("a"), Present("b"), Present("c")],
                Settings = [Row("x", "1", "1", "1")],
            },
        ],
    };

    private static SettingValue Present(string host) => new() { HostId = host, State = SettingValueState.Value, Value = "present" };

    private static SettingValue Value(string host, string value, bool differs) => new() { HostId = host, State = SettingValueState.Value, Value = value, DiffersFromBaseline = differs };

    private static SettingValue Masked(string host, bool differs) => new() { HostId = host, State = SettingValueState.Value, Masked = true, DiffersFromBaseline = differs };

    private static SettingRow Row(string key, string a, string b, string c)
    {
        var differs = !string.Equals(a, c, StringComparison.Ordinal) || !string.Equals(a, b, StringComparison.Ordinal);
        return new SettingRow
        {
            Key = key,
            Differs = differs,
            Values = [Value("a", a, false), Value("b", b, !string.Equals(a, b, StringComparison.Ordinal)), Value("c", c, !string.Equals(a, c, StringComparison.Ordinal))],
        };
    }
}
