using System.Numerics;

using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Profiles.Detection;

/// <summary><c>diff_summary_snapshots</c>.</summary>
public sealed partial class DetectionProfileStore
{
    private sealed record SummaryEntry(HashSet<object?> ConfigSet, BigInteger ConfigCount);

    /// <summary>
    /// <c>diff_summary_snapshots(baseline, current)</c> over two summary payloads (a <see cref="Summary"/> result or its JSON):
    /// <c>totals</c> (<c>baseline</c> and <c>current</c>, each <c>profiles</c> and <c>configs</c>), <c>added_profiles</c> and
    /// <c>removed_profiles</c> sorted, and <c>changed_profiles</c>, in name order, for each profile on both sides whose config ids
    /// or config count differ, with <c>name</c>, <c>baseline_config_count</c>, <c>current_config_count</c> and the sorted
    /// <c>added_config_ids</c> and <c>removed_config_ids</c>.
    /// </summary>
    /// <remarks>
    /// Values are read as Python reads them: counts through <c>int()</c> with the fallback on <c>TypeError</c> or
    /// <c>ValueError</c>, totals of zero replaced by the computed ones, entries without a truthy <c>name</c> skipped, names and
    /// ids compared with Python <c>==</c> and hashing (<see cref="PythonValues"/>) and sorted with Python's <c>&lt;</c>, so a
    /// payload Python rejects raises the same error type.
    /// </remarks>
    public static OrderedDictionary<string, object?> DiffSummarySnapshots(object? baseline, object? current)
    {
        var (baselineProfiles, baselineTotals) = ProfileMap(baseline);
        var (currentProfiles, currentTotals) = ProfileMap(current);

        var addedProfiles = PythonValues.Sorted(currentProfiles.Keys.Where(name => !baselineProfiles.ContainsKey(name)));
        var removedProfiles = PythonValues.Sorted(baselineProfiles.Keys.Where(name => !currentProfiles.ContainsKey(name)));

        var changedProfiles = new List<object?>();
        // set & set iterates the smaller set (the right operand on a tie) and keeps its keys.
        var common = currentProfiles.Count <= baselineProfiles.Count
            ? currentProfiles.Keys.Where(baselineProfiles.ContainsKey)
            : baselineProfiles.Keys.Where(currentProfiles.ContainsKey);
        foreach (var name in PythonValues.Sorted(common))
        {
            var baseEntry = baselineProfiles[name!];
            var currEntry = currentProfiles[name!];
            var addedIds = PythonValues.Sorted(currEntry.ConfigSet.Where(id => !baseEntry.ConfigSet.Contains(id)));
            var removedIds = PythonValues.Sorted(baseEntry.ConfigSet.Where(id => !currEntry.ConfigSet.Contains(id)));
            if (addedIds.Count > 0 || removedIds.Count > 0 || baseEntry.ConfigCount != currEntry.ConfigCount)
            {
                changedProfiles.Add(new OrderedDictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["name"] = name,
                    ["baseline_config_count"] = PythonValues.Narrow(baseEntry.ConfigCount),
                    ["current_config_count"] = PythonValues.Narrow(currEntry.ConfigCount),
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

    // _profile_map: the entries by name (a repeated name keeps its first key and takes the last entry) and the totals.
    private static (Dictionary<object, SummaryEntry> Entries, OrderedDictionary<string, object?> Totals) ProfileMap(object? summary)
    {
        var entries = new Dictionary<object, SummaryEntry>(PythonValues.HashKeys!);
        var profiles = AsInt(PythonBuiltins.Get(summary, "total_profiles"), BigInteger.Zero);
        var configs = AsInt(PythonBuiltins.Get(summary, "total_configs"), BigInteger.Zero);
        var computedConfigTotal = BigInteger.Zero;
        foreach (var entry in PythonBuiltins.Iterate(GetOrDefault(summary, "profiles", EmptyList)))
        {
            var name = PythonBuiltins.Get(entry, "name");
            if (!PythonBuiltins.IsTruthy(name))
            {
                continue;
            }

            var configIds = PythonBuiltins.Iterate(GetOrDefault(entry, "config_ids", EmptyList)).ToList();
            var configCount = AsInt(PythonBuiltins.Get(entry, "config_count"), configIds.Count);
            computedConfigTotal += configCount;
            var configSet = new HashSet<object?>(PythonValues.HashKeys);
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
            ["profiles"] = PythonValues.Narrow(profiles),
            ["configs"] = PythonValues.Narrow(configs),
        };
        return (entries, totals);
    }

    // _as_int: int(value), or the fallback where int() raises TypeError or ValueError.
    private static BigInteger AsInt(object? value, BigInteger fallback)
    {
        try
        {
            return PythonBuiltins.Int(value);
        }
        catch (Exception exc) when (exc is PythonTypeException or PythonValueException)
        {
            return fallback;
        }
    }
}
