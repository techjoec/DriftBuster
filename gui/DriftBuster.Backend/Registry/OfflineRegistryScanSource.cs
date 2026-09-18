using System.Collections;
using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;

using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Profiles.Run;

namespace DriftBuster.Backend.Registry;

/// <summary>
/// <c>offline_runner.OfflineRegistryScanSource</c>: a <c>registry_scan</c> source of an offline runner profile. The token, keywords
/// and patterns (pattern text, compiled when the scan runs), the search limits, the manifest alias, the remote target and batch,
/// and explicit roots that replace the suggested ones. Values are in the <see cref="EngineJson"/> domain.
/// </summary>
public sealed partial record OfflineRegistryScanSource(string Token)
{
    [GeneratedRegex(@"[\s,;]+", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex SequenceSplit();
    private static readonly string[] BatchKeys = ["remote_batch", "remoteTargets", "remote_targets", "batch"];

    public IReadOnlyList<string> Keywords { get; init; } = [];

    public IReadOnlyList<string> Patterns { get; init; } = [];

    public BigInteger MaxDepth { get; init; } = 12;

    public BigInteger MaxHits { get; init; } = 200;

    public double TimeBudgetS { get; init; } = 10.0;

    public string? Alias { get; init; }

    public RemoteRegistryTarget? Remote { get; init; }

    public IReadOnlyList<RemoteRegistryTarget> RemoteBatch { get; init; } = [];

    public IReadOnlyList<RegistryRoot> Roots { get; init; } = [];

    /// <summary>
    /// <c>OfflineRegistryScanSource.from_dict(payload)</c>: <c>payload["registry_scan"]</c> must be a mapping with a non-blank
    /// <c>token</c>; <c>keywords</c> and <c>patterns</c> split a str on runs of whitespace, "," and ";" or take each non-blank
    /// stripped <c>str()</c> of a list; the payload's non-blank <c>alias</c>; <c>remote</c> and the first truthy of
    /// <c>remote_batch</c>, <c>remoteTargets</c>, <c>remote_targets</c> and <c>batch</c> through
    /// <see cref="RemoteRegistryTarget.FromPayload"/> (a mapping is one target, a list each entry, anything else one target);
    /// <c>int(max_depth)</c>, <c>int(max_hits)</c>, <c>float(time_budget_s)</c>; and <c>roots</c> through <see cref="NormaliseRoots"/>.
    /// </summary>
    /// <exception cref="InvalidDataException">Each refusal.</exception>
    /// <exception cref="FormatException">A root descriptor or a number that does not parse.</exception>
    public static OfflineRegistryScanSource FromDict(IReadOnlyDictionary<string, object?> payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.GetValueOrDefault("registry_scan") is not IReadOnlyDictionary<string, object?> spec)
        {
            throw new InvalidDataException("registry_scan source requires an object payload");
        }

        var tokenRaw = spec.GetValueOrDefault("token");
        if (!EngineBuiltins.IsTruthy(tokenRaw) || EngineText.Strip(EngineRepr.Str(tokenRaw)).Length == 0)
        {
            throw new InvalidDataException("registry_scan requires non-empty 'token'.");
        }

        var alias = payload.GetValueOrDefault("alias");
        if (alias is not null && EngineText.Strip(EngineRepr.Str(alias)).Length == 0)
        {
            alias = null;
        }

        var remoteSpec = spec.GetValueOrDefault("remote");
        var remote = remoteSpec is not null ? RemoteRegistryTarget.FromPayload(remoteSpec) : null;
        var batch = ReadBatch(BatchKeys.Select(key => spec.GetValueOrDefault(key)).FirstOrDefault(EngineBuiltins.IsTruthy, spec.GetValueOrDefault("batch")));

        return new OfflineRegistryScanSource(EngineText.Strip(EngineRepr.Str(tokenRaw)))
        {
            Keywords = NormaliseSequence(spec.GetValueOrDefault("keywords")),
            Patterns = NormaliseSequence(spec.GetValueOrDefault("patterns")),
            MaxDepth = EngineBuiltins.Int(spec.TryGetValue("max_depth", out var depth) ? depth : 12),
            MaxHits = EngineBuiltins.Int(spec.TryGetValue("max_hits", out var hits) ? hits : 200),
            TimeBudgetS = EngineBuiltins.Float(spec.TryGetValue("time_budget_s", out var budget) ? budget : 10.0),
            Alias = EngineBuiltins.IsTruthy(alias) ? EngineRepr.Str(alias) : null,
            Remote = remote,
            RemoteBatch = batch,
            Roots = NormaliseRoots(spec.GetValueOrDefault("roots")),
        };
    }

    private static List<RemoteRegistryTarget> ReadBatch(object? batchSpec)
    {
        var batch = new List<RemoteRegistryTarget>();
        if (batchSpec is null)
        {
            return batch;
        }

        if (batchSpec is not IReadOnlyDictionary<string, object?> && batchSpec is IList list and not byte[])
        {
            batch.AddRange(list.Cast<object?>().Select(RemoteRegistryTarget.FromPayload));
        }
        else
        {
            batch.Add(RemoteRegistryTarget.FromPayload(batchSpec));
        }

        return batch;
    }

    // _norm_seq(value)
    private static List<string> NormaliseSequence(object? value)
    {
        if (!EngineBuiltins.IsTruthy(value))
        {
            return [];
        }

        if (value is string text)
        {
            return SequenceSplit().Split(text).Where(part => part.Length > 0).ToList();
        }

        if (value is IReadOnlyDictionary<string, object?> || value is not IList list)
        {
            return [];
        }

        return list.Cast<object?>().Select(EngineRepr.Str).Where(item => EngineText.Strip(item).Length > 0).Select(EngineText.Strip).ToList();
    }

    /// <summary>
    /// <c>_normalise_registry_roots(value)</c>: nothing for a falsy value; a str or mapping (or any other non-list) is one entry. A str
    /// entry is <see cref="RegistryRoot.Parse"/>; a mapping needs non-blank <c>str(hive)</c> and <c>str(path)</c> (stripped, the hive
    /// upper-cased) and reads a non-blank <c>view</c> as 32, 64 or auto; anything else is refused.
    /// </summary>
    /// <exception cref="InvalidDataException">Each refusal of a mapping or another value.</exception>
    /// <exception cref="FormatException">A root descriptor that does not parse.</exception>
    public static IReadOnlyList<RegistryRoot> NormaliseRoots(object? value)
    {
        if (!EngineBuiltins.IsTruthy(value))
        {
            return [];
        }

        var isList = value is IList and not IReadOnlyDictionary<string, object?> and not byte[];
        IEnumerable<object?> entries = isList ? ((IList)value!).Cast<object?>() : [value];
        var normalised = new List<RegistryRoot>();
        foreach (var entry in entries)
        {
            normalised.Add(entry switch
            {
                RegistryRoot root => root,
                string text => RegistryRoot.Parse(text),
                IReadOnlyDictionary<string, object?> mapping => RootFromMapping(mapping),
                _ => throw new InvalidDataException("registry_scan roots entries must be strings or mappings"),
            });
        }

        return normalised.AsReadOnly();
    }

    private static RegistryRoot RootFromMapping(IReadOnlyDictionary<string, object?> mapping)
    {
        var hive = EngineText.Strip(EngineRepr.Str(mapping.TryGetValue("hive", out var hiveValue) ? hiveValue : string.Empty));
        var path = EngineText.Strip(EngineRepr.Str(mapping.TryGetValue("path", out var pathValue) ? pathValue : string.Empty));
        if (hive.Length == 0 || path.Length == 0)
        {
            throw new InvalidDataException("registry_scan roots entries require 'hive' and 'path'");
        }

        var viewRaw = mapping.GetValueOrDefault("view");
        string? view = null;
        if (viewRaw is not null && EngineText.Strip(EngineRepr.Str(viewRaw)).Length > 0)
        {
            view = RegistryText.Upper(EngineText.Strip(EngineRepr.Str(viewRaw))) switch
            {
                "AUTO" => null,
                "32" => "32",
                "64" => "64",
                _ => throw new InvalidDataException("registry_scan root view must be 32, 64, or auto"),
            };
        }

        return new RegistryRoot(RegistryText.Upper(hive), path, view);
    }

    /// <summary>
    /// <c>source.destination_name(fallback_index=fallbackIndex)</c>: the safe alias, otherwise the safe <c>registry_{token}</c>
    /// (<c>registry_registry_NN</c> for an empty token).
    /// </summary>
    public string DestinationName(int fallbackIndex)
    {
        if (!string.IsNullOrEmpty(Alias))
        {
            return RunProfileStore.SafeName(Alias);
        }

        // f"{index:02d}": zero-padded to two characters, the sign counted.
        var index = fallbackIndex >= 0
            ? fallbackIndex.ToString("00", CultureInfo.InvariantCulture)
            : fallbackIndex.ToString(CultureInfo.InvariantCulture);
        var baseName = Token.Length > 0 ? Token : $"registry_{index}";
        return RunProfileStore.SafeName($"registry_{baseName}");
    }
}
