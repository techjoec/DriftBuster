using DriftBuster.Backend.Models;

namespace DriftBuster.Backend.Settings;

/// <summary>
/// Recounts a comparison after curation: which settings and files differ and each server's summary, leaving out ignored files,
/// settings and values. The counting rules are the builder's: a setting counts for a server when that server's value differs
/// from the baseline's and both have the file; a file counts when a setting differs or its presence differs.
/// </summary>
public static class SettingsTally
{
    public static void Recount(SettingsComparison comparison)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        var tallies = comparison.Hosts.ToDictionary(host => host.HostId, _ => new Tally(), StringComparer.Ordinal);

        foreach (var file in comparison.Files)
        {
            foreach (var row in file.Settings)
            {
                row.Differs = !file.Ignored && !row.Ignored && row.Values.Any(value => value.DiffersFromBaseline && !value.Ignored);
            }

            file.SettingsDiffering = file.Settings.Count(row => row.Differs);
            file.Differs = !file.Ignored && (file.SettingsDiffering > 0 || file.Presence.Any(value => value.DiffersFromBaseline));
            if (file.Ignored)
            {
                continue;
            }

            var baselineState = file.Presence.FirstOrDefault(value => string.Equals(value.HostId, comparison.BaselineHostId, StringComparison.Ordinal))?.State
                ?? SettingValueState.NotScanned;
            foreach (var host in comparison.Hosts.Where(host => !host.IsBaseline && host.Scanned))
            {
                Count(tallies[host.HostId], host.HostId, file, baselineState);
            }
        }

        foreach (var host in comparison.Hosts)
        {
            if (host.IsBaseline || !host.Scanned)
            {
                continue;
            }

            var tally = tallies[host.HostId];
            host.SettingsDiffering = tally.SettingsDiffering;
            host.FilesDiffering = tally.FilesDiffering;
            host.FilesMissing = tally.FilesMissing.ToArray();
            host.FilesExtra = tally.FilesExtra.ToArray();
            host.FilesUnreadable = tally.FilesUnreadable.ToArray();
            host.MatchesBaseline = tally.FilesDiffering == 0 && tally.FilesMissing.Count == 0 && tally.FilesExtra.Count == 0 && tally.FilesUnreadable.Count == 0;
        }

    }

    private static void Count(Tally tally, string hostId, FileComparison file, SettingValueState baselineState)
    {
        var state = file.Presence.FirstOrDefault(value => string.Equals(value.HostId, hostId, StringComparison.Ordinal))?.State ?? SettingValueState.NotScanned;
        if (state == SettingValueState.Unreadable)
        {
            tally.FilesUnreadable.Add(file.Path);
        }

        if (baselineState == SettingValueState.Value && state == SettingValueState.FileMissing)
        {
            tally.FilesMissing.Add(file.Path);
        }
        else if (state == SettingValueState.Value && baselineState == SettingValueState.FileMissing)
        {
            tally.FilesExtra.Add(file.Path);
        }

        var differing = file.Settings.Count(row => !row.Ignored
            && row.Values.Any(value => value.DiffersFromBaseline && !value.Ignored && string.Equals(value.HostId, hostId, StringComparison.Ordinal)));
        if (state == SettingValueState.Value && baselineState == SettingValueState.Value)
        {
            tally.SettingsDiffering += differing;
        }

        if (differing > 0 || file.Presence.Any(value => value.DiffersFromBaseline && string.Equals(value.HostId, hostId, StringComparison.Ordinal)))
        {
            tally.FilesDiffering++;
        }
    }

    private sealed class Tally
    {
        public int SettingsDiffering { get; set; }

        public int FilesDiffering { get; set; }

        public List<string> FilesMissing { get; } = [];

        public List<string> FilesExtra { get; } = [];

        public List<string> FilesUnreadable { get; } = [];
    }
}
