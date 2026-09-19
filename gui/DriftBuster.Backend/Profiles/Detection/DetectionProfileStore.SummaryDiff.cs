using System.Numerics;

using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Profiles.Detection;

/// <summary>Diffing two store summaries.</summary>
public sealed partial class DetectionProfileStore
{
    private sealed record SummaryEntry(HashSet<object?> ConfigSet, BigInteger ConfigCount);

    /// <summary>
    /// Compares two summary payloads (a <see cref="Summary"/> result or its JSON): <c>totals</c> (baseline/current profiles and configs),
    /// sorted <c>added_profiles</c> and <c>removed_profiles</c>, and <c>changed_profiles</c> in name order for profiles whose config ids or
    /// counts differ (<c>name</c>, both counts, sorted <c>added_config_ids</c> and <c>removed_config_ids</c>).
    /// </summary>
    /// <remarks>
    /// Counts that are not integers fall back to the computed ones, zero totals are replaced by computed totals, entries without a name
    /// are skipped; names and ids compare and sort with <see cref="EngineValues"/>, so mixed kinds throw.
    /// </remarks>
    public static OrderedDictionary<string, object?> DiffSummarySnapshots(object? baseline, object? current)
    {
        var (baselineProfiles, baselineTotals) = ProfileMap(baseline);
        var (currentProfiles, currentTotals) = ProfileMap(current);

        var addedProfiles = EngineValues.Sorted(currentProfiles.Keys.Where(name => !baselineProfiles.ContainsKey(name)));
        var removedProfiles = EngineValues.Sorted(baselineProfiles.Keys.Where(name => !currentProfiles.ContainsKey(name)));

        var changedProfiles = new List<object?>();
        // The intersection iterates the smaller set (the current side on a tie).
        var common = currentProfiles.Count <= baselineProfiles.Count
            ? currentProfiles.Keys.Where(baselineProfiles.ContainsKey)
            : baselineProfiles.Keys.Where(currentProfiles.ContainsKey);
        foreach (var name in EngineValues.Sorted(common))
        {
            var baseEntry = baselineProfiles[name!];
            var currEntry = currentProfiles[name!];
            var addedIds = EngineValues.Sorted(currEntry.ConfigSet.Where(id => !baseEntry.ConfigSet.Contains(id)));
            var removedIds = EngineValues.Sorted(baseEntry.ConfigSet.Where(id => !currEntry.ConfigSet.Contains(id)));
            if (addedIds.Count > 0 || removedIds.Count > 0 || baseEntry.ConfigCount != currEntry.ConfigCount)
            {
                changedProfiles.Add(new OrderedDictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["name"] = name,
                    ["baseline_config_count"] = EngineValues.Narrow(baseEntry.ConfigCount),
                    ["current_config_count"] = EngineValues.Narrow(currEntry.ConfigCount),
                    ["added_config_ids"] = addedIds,
                    ["removed_config_ids"] = removedIds,
                });
            }
        }

        return new OrderedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["totals"] = new OrderedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["baseline"] = baselineTotals,
                ["current"] = currentTotals,
            },
            ["added_profiles"] = addedProfiles,
            ["removed_profiles"] = removedProfiles,
            ["changed_profiles"] = changedProfiles,
        };
    }

    // Entries by name (a repeated name keeps its first slot and takes the last entry) and the totals.
    private static (Dictionary<object, SummaryEntry> Entries, OrderedDictionary<string, object?> Totals) ProfileMap(object? summary)
    {
        var entries = new Dictionary<object, SummaryEntry>(EngineValues.HashKeys!);
        var profiles = AsInt(EngineBuiltins.Get(summary, "total_profiles"), BigInteger.Zero);
        var configs = AsInt(EngineBuiltins.Get(summary, "total_configs"), BigInteger.Zero);
        var computedConfigTotal = BigInteger.Zero;
        foreach (var entry in EngineBuiltins.Iterate(GetOrDefault(summary, "profiles", EmptyList)))
        {
            var name = EngineBuiltins.Get(entry, "name");
            if (!EngineBuiltins.IsTruthy(name))
            {
                continue;
            }

            var configIds = EngineBuiltins.Iterate(GetOrDefault(entry, "config_ids", EmptyList)).ToList();
            var configCount = AsInt(EngineBuiltins.Get(entry, "config_count"), configIds.Count);
            computedConfigTotal += configCount;
            var configSet = new HashSet<object?>(EngineValues.HashKeys);
            foreach (var id in configIds)
            {
                configSet.Add(id);
            }

            entries[name!] = new SummaryEntry(configSet, configCount);
        }

        if (profiles.IsZero)
        {
            profiles = entries.Count;
        }

        if (configs.IsZero)
        {
            configs = computedConfigTotal;
        }

        var totals = new OrderedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["profiles"] = EngineValues.Narrow(profiles),
            ["configs"] = EngineValues.Narrow(configs),
        };
        return (entries, totals);
    }

    // The value as an integer, or the fallback.
    private static BigInteger AsInt(object? value, BigInteger fallback)
    {
        try
        {
            return EngineBuiltins.Int(value);
        }
        catch (Exception exc) when (exc is InvalidDataException or FormatException)
        {
            return fallback;
        }
    }
}
