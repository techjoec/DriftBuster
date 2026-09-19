using System.Diagnostics;
using System.Text;

using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.MultiServer;

namespace DriftBuster.Backend.Registry;

public static partial class RegistryScan
{
    internal const int PreviewLength = 120;

    private static readonly UTF8Encoding ReplacingUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    /// <summary>
    /// Breadth-first from each root, each (hive, path, view) once. Values are matched in backend order (<see cref="MatchValue"/>);
    /// subkeys are queued while depth is below max_depth (at least 0). Stops at max_hits (at least 1) or after time_budget_s
    /// (at least 0.1), checked before each key. Backend exceptions propagate.
    /// </summary>
    public static IReadOnlyList<RegistryHit> SearchRegistry(IEnumerable<RegistryRoot> roots, SearchSpec spec, IRegistryBackend? backend = null)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(spec);
        backend ??= DefaultBackend();
        var keywords = spec.Keywords.Select(EngineText.Lower).ToList();
        var maxDepth = Math.Max(0, spec.MaxDepth);
        var maxHits = Math.Max(1, spec.MaxHits);
        var budget = spec.TimeBudgetS > 0.1 ? spec.TimeBudgetS : 0.1;
        var started = Stopwatch.GetTimestamp();

        var hits = new List<RegistryHit>();
        var queue = new Queue<(RegistryRoot Key, long Depth)>(roots.Select(root => (root, 0L)));
        var seen = new HashSet<RegistryRoot>();
        // Roots that differ only in view can reach the same key twice (the default view is the 64-bit one in a 64-bit
        // process); a hit with the same hive, path, name and data is reported once. Names compare case-insensitively.
        var reported = new HashSet<(string Hive, string Path, string Name, string Preview)>();
        while (queue.Count > 0 && hits.Count < maxHits && Stopwatch.GetElapsedTime(started).TotalSeconds < budget)
        {
            var (key, depth) = queue.Dequeue();
            if (!seen.Add(key))
            {
                continue;
            }

            CollectHits(backend.EnumValues(key.Hive, key.Path, key.View), key, keywords, spec, hits, reported, maxHits);
            if (hits.Count >= maxHits)
            {
                break;
            }

            if (depth >= maxDepth)
            {
                continue;
            }

            foreach (var child in backend.EnumSubkeys(key.Hive, key.Path, key.View))
            {
                queue.Enqueue((key with { Path = $"{key.Path}\\{child}" }, depth + 1));
            }
        }

        return hits.AsReadOnly();
    }

    // The key's values that match, skipping any hit already reported, until the hit limit.
    private static void CollectHits(
        IReadOnlyList<KeyValuePair<string, object?>> values,
        RegistryRoot key,
        List<string> keywords,
        SearchSpec spec,
        List<RegistryHit> hits,
        HashSet<(string Hive, string Path, string Name, string Preview)> reported,
        long maxHits)
    {
        foreach (var (name, data) in values)
        {
            var preview = MatchValue(name, data, keywords, spec);
            if (preview is null || !reported.Add((key.Hive, key.Path.ToUpperInvariant(), name.ToUpperInvariant(), preview)))
            {
                continue;
            }

            hits.Add(new RegistryHit(key.Path, key.Hive, name, preview, "keyword/pattern match"));
            if (hits.Count >= maxHits)
            {
                return;
            }
        }
    }

    /// <summary>
    /// The value's text (<see cref="ValueText"/>) when every lower-cased keyword occurs in "{name} {text}" (lower-cased) and, with
    /// patterns, one of them finds the text or the name; the first <see cref="PreviewLength"/> code points of it, else null.
    /// </summary>
    internal static string? MatchValue(string name, object? value, IReadOnlyList<string> keywords, SearchSpec spec)
    {
        var text = ValueText(value);
        if (text is null)
        {
            return null;
        }

        var combined = $"{EngineText.Lower(name)} {EngineText.Lower(text)}";
        if (keywords.Count > 0 && !keywords.All(keyword => EngineText.Contains(combined, keyword)))
        {
            return null;
        }

        if (spec.Patterns.Count > 0
            && !(spec.Patterns.Any(pattern => PatternRegex.IsMatch(pattern, text)) || spec.Patterns.Any(pattern => PatternRegex.IsMatch(pattern, name))))
        {
            return null;
        }

        return MultiServerRunner.TruncateCodePoints(text, PreviewLength);
    }

    // Text of a value: a string as is, bytes as UTF-8 with replacement, numbers and bools as text, lists joined by ", "; else null.
    internal static string? ValueText(object? value)
    {
        switch (value)
        {
            case string text:
                return text;
            case byte[] bytes:
                return ReplacingUtf8.GetString(bytes);
            case bool or int or long or System.Numerics.BigInteger or double:
                return EngineRepr.Str(value);
            case var list when RegistryText.IsList(list):
                try
                {
                    // Items joined by ", ": bytes as b'...', nested containers in display form.
                    return string.Join(", ", ((System.Collections.IList)list!).Cast<object?>().Select(EngineRepr.Str));
                }
                catch (ArgumentException)
                {
                    return null;
                }

            default:
                return null;
        }
    }
}
