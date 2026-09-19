using System.Globalization;

using DriftBuster.Backend.Curation;
using DriftBuster.Backend.Detection;
using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Json;
using DriftBuster.Backend.Models;
using DriftBuster.Backend.MultiServer;

namespace DriftBuster.Backend.Settings;

/// <summary>
/// Compares every scanned file setting by setting. Files are matched across servers by their path relative to the scanned
/// root (case-insensitively, as Windows paths are), so a copy detected differently on one server still lines up. Each file is
/// compared with the baseline server's copy, or with the first readable copy when the baseline has none. Secret values are
/// compared but never copied into the result.
/// </summary>
public static class SettingsComparisonBuilder
{
    private const string PresentState = "present";

    private sealed record HostCopy(ConfigRecord? Record, ExtractedSettings? Settings, SettingValueState State);

    public static SettingsComparison Build(
        IReadOnlyList<MultiServerPlan> plans,
        IReadOnlyDictionary<string, OrderedDictionary<string, ConfigRecord>> hostConfigs,
        IReadOnlyDictionary<string, IReadOnlyList<string>> hostUnreadable,
        IReadOnlyList<ServerScanResult> hostResults,
        string baselineHostId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plans);
        ArgumentNullException.ThrowIfNull(hostConfigs);
        ArgumentNullException.ThrowIfNull(hostUnreadable);
        ArgumentNullException.ThrowIfNull(hostResults);
        ArgumentNullException.ThrowIfNull(baselineHostId);

        var hosts = plans.Select(plan => plan.HostId).Distinct(StringComparer.Ordinal).ToList();
        var labels = plans.GroupBy(plan => plan.HostId, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Last().Label, StringComparer.Ordinal);
        var results = hostResults.GroupBy(result => result.HostId, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
        bool Scanned(string hostId) => results.TryGetValue(hostId, out var result) && result.Status == ServerScanStatus.Succeeded;

        var (byPath, unreadable) = IndexFiles(hostConfigs, hostUnreadable);

        var summaries = hosts.ToDictionary(
            hostId => hostId,
            hostId => new HostTally(hostId, labels[hostId], string.Equals(hostId, baselineHostId, StringComparison.Ordinal), Scanned(hostId), results.TryGetValue(hostId, out var result) ? result.Message : string.Empty),
            StringComparer.Ordinal);

        var files = new List<FileComparison>();
        foreach (var (path, perHost) in byPath)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var copies = hosts.ToDictionary(
                hostId => hostId,
                hostId => !Scanned(hostId) ? new HostCopy(null, null, SettingValueState.NotScanned)
                    : perHost.TryGetValue(hostId, out var record) ? new HostCopy(record, SettingsExtractor.Extract(record), SettingValueState.Value)
                    : unreadable.TryGetValue(path, out var set) && set.Contains(hostId) ? new HostCopy(null, null, SettingValueState.Unreadable)
                    : new HostCopy(null, null, SettingValueState.FileMissing),
                StringComparer.Ordinal);
            files.Add(CompareFile(path, hosts, copies, baselineHostId, summaries, cancellationToken));
        }

        return new SettingsComparison
        {
            BaselineHostId = baselineHostId,
            Hosts = hosts.Select(hostId => summaries[hostId].ToSummary()).ToArray(),
            Files = files.ToArray(),
        };
    }

    // path -> host -> record, and path -> hosts that could not read it; every unreadable path is also a file.
    private static (SortedDictionary<string, Dictionary<string, ConfigRecord>> ByPath, Dictionary<string, HashSet<string>> Unreadable) IndexFiles(
        IReadOnlyDictionary<string, OrderedDictionary<string, ConfigRecord>> hostConfigs,
        IReadOnlyDictionary<string, IReadOnlyList<string>> hostUnreadable)
    {
        var byPath = new SortedDictionary<string, Dictionary<string, ConfigRecord>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (hostId, configs) in hostConfigs)
        {
            foreach (var record in configs.Values)
            {
                var path = NormalisePath(record.RelativePath);
                if (!byPath.TryGetValue(path, out var perHost))
                {
                    perHost = new Dictionary<string, ConfigRecord>(StringComparer.Ordinal);
                    byPath[path] = perHost;
                }

                perHost.TryAdd(hostId, record);
            }
        }

        var unreadable = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (hostId, paths) in hostUnreadable)
        {
            foreach (var path in paths.Select(NormalisePath))
            {
                if (!unreadable.TryGetValue(path, out var set))
                {
                    set = new HashSet<string>(StringComparer.Ordinal);
                    unreadable[path] = set;
                }

                set.Add(hostId);
                if (!byPath.ContainsKey(path))
                {
                    byPath[path] = new Dictionary<string, ConfigRecord>(StringComparer.Ordinal);
                }
            }
        }

        return (byPath, unreadable);
    }

    private static FileComparison CompareFile(
        string path,
        List<string> hosts,
        Dictionary<string, HostCopy> copies,
        string baselineHostId,
        Dictionary<string, HostTally> summaries,
        CancellationToken cancellationToken)
    {
        // The reference copy: the baseline's when it can be read, else the first readable one.
        var referenceHostId = copies.TryGetValue(baselineHostId, out var baselineCopy) && baselineCopy.Settings is not null
            ? baselineHostId
            : hosts.FirstOrDefault(hostId => copies[hostId].Settings is not null);
        var reference = referenceHostId is null ? null : copies[referenceHostId];
        var referenceRecord = reference?.Record;

        var file = new FileComparison
        {
            ConfigId = referenceRecord?.ConfigId ?? hosts.Select(hostId => copies[hostId].Record?.ConfigId).FirstOrDefault(id => id is not null) ?? string.Empty,
            Path = path,
            Format = referenceRecord?.FormatId ?? string.Empty,
            Confidence = referenceRecord?.Confidence ?? 0,
            Candidates = referenceRecord is null ? [] : [.. referenceRecord.Candidates],
            Mode = reference?.Settings?.Mode switch
            {
                SettingsMode.Lines => "lines",
                SettingsMode.Binary => "binary",
                SettingsMode.Parsed => "settings",
                _ => string.Empty,
            },
        };

        var baselineState = copies.TryGetValue(baselineHostId, out var baselineHost) ? baselineHost.State : SettingValueState.NotScanned;
        file.Presence = hosts.Select(hostId =>
        {
            var state = copies[hostId].State;
            var differs = !string.Equals(hostId, baselineHostId, StringComparison.Ordinal)
                && state != SettingValueState.NotScanned && baselineState != SettingValueState.NotScanned && state != baselineState;
            return new SettingValue { HostId = hostId, State = state, Value = state == SettingValueState.Value ? PresentState : null, DiffersFromBaseline = differs };
        }).ToArray();

        var rows = BuildRows(hosts, copies, referenceHostId, cancellationToken);
        file.Settings = rows;
        file.SettingsDiffering = rows.Count(row => row.Differs);
        file.Differs = file.SettingsDiffering > 0 || file.Presence.Any(value => value.DiffersFromBaseline);

        Tally(path, hosts, copies, baselineState, file, rows, summaries);
        return file;
    }

    private static void Tally(
        string path,
        List<string> hosts,
        Dictionary<string, HostCopy> copies,
        SettingValueState baselineState,
        FileComparison file,
        SettingRow[] rows,
        Dictionary<string, HostTally> summaries)
    {
        foreach (var hostId in hosts)
        {
            var tally = summaries[hostId];
            if (tally.IsBaseline || !tally.Scanned)
            {
                continue;
            }

            var state = copies[hostId].State;
            if (state == SettingValueState.Unreadable)
            {
                tally.FilesUnreadable.Add(path);
            }

            if (baselineState == SettingValueState.Value && state == SettingValueState.FileMissing)
            {
                tally.FilesMissing.Add(path);
            }
            else if (state == SettingValueState.Value && baselineState == SettingValueState.FileMissing)
            {
                tally.FilesExtra.Add(path);
            }

            var differing = rows.Count(row => row.Values.Any(value => value.DiffersFromBaseline && string.Equals(value.HostId, hostId, StringComparison.Ordinal)));
            if (state == SettingValueState.Value && baselineState == SettingValueState.Value)
            {
                tally.SettingsDiffering += differing;
            }

            if (differing > 0 || file.Presence.Any(value => value.DiffersFromBaseline && string.Equals(value.HostId, hostId, StringComparison.Ordinal)))
            {
                tally.FilesDiffering++;
            }
        }

    }

    private static SettingRow[] BuildRows(List<string> hosts, Dictionary<string, HostCopy> copies, string? referenceHostId, CancellationToken cancellationToken)
    {
        // Keys in the reference copy's order, then keys only other copies have, in their order.
        var keys = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var lookups = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (var hostId in (referenceHostId is null ? hosts : hosts.Prepend(referenceHostId)).Distinct(StringComparer.Ordinal))
        {
            if (copies[hostId].Settings is not { } settings)
            {
                continue;
            }

            var lookup = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var entry in settings.Entries)
            {
                lookup[entry.Key] = entry.Value;
                if (seen.Add(entry.Key))
                {
                    keys.Add(entry.Key);
                }
            }

            lookups[hostId] = lookup;
        }

        var rows = new SettingRow[keys.Count];
        for (var index = 0; index < keys.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = keys[index];
            var raw = hosts.ToDictionary(
                hostId => hostId,
                hostId => lookups.TryGetValue(hostId, out var lookup)
                    ? lookup.TryGetValue(key, out var value) ? (State: SettingValueState.Value, Value: value) : (State: SettingValueState.NotSet, Value: (string?)null)
                    : (State: copies[hostId].State, Value: (string?)null),
                StringComparer.Ordinal);
            var masked = raw.Values.Any(item => item.Value is not null && SettingSecrets.IsSecret(key, item.Value, cancellationToken));
            var referenceValue = referenceHostId is null ? default : raw[referenceHostId];
            var values = hosts.Select(hostId =>
            {
                var (state, value) = raw[hostId];
                var differs = referenceHostId is not null
                    && !string.Equals(hostId, referenceHostId, StringComparison.Ordinal)
                    && state is SettingValueState.Value or SettingValueState.NotSet
                    && (state != referenceValue.State || !string.Equals(value, referenceValue.Value, StringComparison.Ordinal));
                return new SettingValue
                {
                    HostId = hostId,
                    State = state,
                    Value = masked ? null : value,
                    Masked = masked && state == SettingValueState.Value,
                    DiffersFromBaseline = differs,
                    ValueHash = value is null ? null : CurationTarget.ValueHashOf(value),
                    SecretValue = masked ? value : null,
                };
            }).ToArray();
            rows[index] = new SettingRow { Key = key, Values = values, Differs = values.Any(value => value.DiffersFromBaseline) };
        }

        return rows;
    }

    private static string NormalisePath(string path) => path.Replace('\\', '/');

    /// <summary>
    /// Compares files the user picked (the first is the baseline) setting by setting, each file as a column labelled by the part
    /// of its path that tells it apart. The format comes from detection, as in a scan.
    /// </summary>
    public static SettingsComparison CompareFiles(IReadOnlyList<(string Path, string Text)> files, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (files.Count == 0)
        {
            return new SettingsComparison();
        }

        var detector = new Detector(onWarning: static _ => { });
        var shared = PathText.Name(files[0].Path);
        var labels = ColumnLabels(files.Select(file => file.Path).ToList());
        var plans = new List<MultiServerPlan>();
        var configs = new Dictionary<string, OrderedDictionary<string, ConfigRecord>>(StringComparer.Ordinal);
        var results = new List<ServerScanResult>();
        for (var index = 0; index < files.Count; index++)
        {
            var (path, text) = files[index];
            var hostId = index.ToString(CultureInfo.InvariantCulture);
            var label = labels[index];
            plans.Add(new MultiServerPlan { HostId = hostId, Label = label, Roots = [path] });
            results.Add(new ServerScanResult { HostId = hostId, Label = label, Status = ServerScanStatus.Succeeded });
            DetectionMatch? match = null;
            try
            {
                match = detector.ScanFile(path);
            }
            catch (Exception exc) when (exc is IOException or UnauthorizedAccessException or MetadataValidationException)
            {
                // Undetectable files are still compared, line by line.
            }

            var format = match?.Metadata.Text("catalog_format") ?? match?.FormatName ?? "text";
            var record = new ConfigRecord
            {
                ConfigId = hostId,
                DisplayName = shared,
                FormatId = format,
                ContentType = "text",
                Canonical = text,
                Raw = text,
                FileHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text))),
                SourcePath = path,
                PluginName = match?.PluginName ?? "text",
                RelativePath = shared,
            };
            configs[hostId] = new OrderedDictionary<string, ConfigRecord>(StringComparer.Ordinal) { [hostId] = record };
        }

        var comparison = Build(plans, configs, new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal), results, "0", cancellationToken);
        foreach (var file in comparison.Files)
        {
            file.ConfigId = string.Empty;
        }

        return comparison;
    }

    /// <summary>
    /// A short column label per path: drop the trailing folders and name every path shares, then keep as few folders before
    /// that as make the labels unique ("baseline", "staging", "prod"). Paths with different names are labelled by name.
    /// </summary>
    internal static IReadOnlyList<string> ColumnLabels(IReadOnlyList<string> paths)
    {
        var parts = paths.Select(path => path.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries)).ToList();
        var shortest = parts.Min(segments => segments.Length);
        var shared = 0;
        while (shared < shortest - 1 && parts.All(segments => string.Equals(segments[^(shared + 1)], parts[0][^(shared + 1)], StringComparison.OrdinalIgnoreCase)))
        {
            shared++;
        }

        for (var depth = 1; depth <= shortest - shared; depth++)
        {
            var labels = parts.Select(segments => string.Join('/', segments[(segments.Length - shared - depth)..(segments.Length - shared)])).ToList();
            if (labels.Distinct(StringComparer.OrdinalIgnoreCase).Count() == labels.Count)
            {
                return labels;
            }
        }

        return paths.ToList();
    }

    private sealed class HostTally(string hostId, string label, bool isBaseline, bool scanned, string message)
    {
        public bool IsBaseline { get; } = isBaseline;

        public bool Scanned { get; } = scanned;

        public int SettingsDiffering { get; set; }

        public int FilesDiffering { get; set; }

        public List<string> FilesMissing { get; } = [];

        public List<string> FilesExtra { get; } = [];

        public List<string> FilesUnreadable { get; } = [];

        public HostComparisonSummary ToSummary() => new()
        {
            HostId = hostId,
            Label = label,
            IsBaseline = IsBaseline,
            Scanned = Scanned,
            ScanMessage = message,
            SettingsDiffering = SettingsDiffering,
            FilesDiffering = FilesDiffering,
            FilesMissing = FilesMissing.ToArray(),
            FilesExtra = FilesExtra.ToArray(),
            FilesUnreadable = FilesUnreadable.ToArray(),
            MatchesBaseline = Scanned && FilesDiffering == 0 && FilesMissing.Count == 0 && FilesExtra.Count == 0 && FilesUnreadable.Count == 0,
        };
    }
}
