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
    /// <c>search_registry(roots, spec, backend=backend)</c>: breadth-first from every root at depth 0, each <c>(hive, path, view)</c>
    /// visited once. A key's values are matched in the backend's order (<see cref="MatchValue"/>) and each match is a hit with reason
    /// <c>"keyword/pattern match"</c>; subkeys are queued while the key's depth is below <c>max(0, max_depth)</c>. The walk stops at
    /// <c>max(1, max_hits)</c> hits and once <c>max(0.1, time_budget_s)</c> seconds have passed since the call, checked before each
    /// key. Backend exceptions propagate.
    /// </summary>
    public static IReadOnlyList<RegistryHit> SearchRegistry(IEnumerable<RegistryRoot> roots, SearchSpec spec, IRegistryBackend? backend = null)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(spec);
        backend ??= DefaultBackend();
        var keywords = spec.Keywords.Select(PythonText.Lower).ToList();
        // max(0, int(spec.max_depth)) and max(1, int(spec.max_hits)); the depth and hit counts they are compared with never leave the
        // long range, so a larger int behaves as long.MaxValue.
        var maxDepth = ClampToLong(System.Numerics.BigInteger.Max(0, spec.MaxDepth));
        var maxHits = ClampToLong(System.Numerics.BigInteger.Max(1, spec.MaxHits));
        var budget = spec.TimeBudgetS > 0.1 ? spec.TimeBudgetS : 0.1;
        var started = Stopwatch.GetTimestamp();

        var hits = new List<RegistryHit>();
        var queue = new Queue<(RegistryRoot Key, long Depth)>(roots.Select(root => (root, 0L)));
        var seen = new HashSet<RegistryRoot>();
        while (queue.Count > 0 && hits.Count < maxHits && Stopwatch.GetElapsedTime(started).TotalSeconds < budget)
        {
            var (key, depth) = queue.Dequeue();
            if (!seen.Add(key))
            {
                continue;
            }

            foreach (var (name, data) in backend.EnumValues(key.Hive, key.Path, key.View))
            {
                var preview = MatchValue(name, data, keywords, spec);
                if (preview is null)
                {
                    continue;
                }

                hits.Add(new RegistryHit(key.Path, key.Hive, name, preview, "keyword/pattern match"));
                if (hits.Count >= maxHits)
                {
                    break;
                }
            }

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

    /// <summary>
    /// <c>_match_value(name, val)</c>: the value's text (a str as itself, bytes decoded as UTF-8 with replacement, an int, bool or
    /// float as <c>str()</c>, a list or tuple as its items' <c>str()</c> joined by ", ", anything else, or a list whose items have
    /// no <c>str()</c>, no text), refused unless every lower-cased keyword is in <c>"{name.lower()} {text.lower()}"</c> and, when there
    /// are patterns, one of them finds the text or the name; the first <see cref="PreviewLength"/> code points of the text.
    /// </summary>
    internal static string? MatchValue(string name, object? value, IReadOnlyList<string> keywords, SearchSpec spec)
    {
        var text = ValueText(value);
        if (text is null)
        {
            return null;
        }

        var combined = $"{PythonText.Lower(name)} {PythonText.Lower(text)}";
        if (keywords.Count > 0 && !keywords.All(keyword => PythonText.Contains(combined, keyword)))
        {
            return null;
        }

        if (spec.Patterns.Count > 0
            && !(spec.Patterns.Any(pattern => pattern.Search(text) is not null) || spec.Patterns.Any(pattern => pattern.Search(name) is not null)))
        {
            return null;
        }

        return MultiServerRunner.TruncateCodePoints(text, PreviewLength);
    }

    // The text branch of _match_value.
    internal static string? ValueText(object? value)
    {
        switch (value)
        {
            case string text:
                return text;
            case byte[] bytes:
                return ReplacingUtf8.GetString(bytes);
            case bool or int or long or System.Numerics.BigInteger or double:
                return PythonRepr.Str(value);
            case var list when RegistryPython.IsList(list):
                try
                {
                    // ", ".join(str(x) for x in val): bytes items spelled b'...', nested containers by their repr.
                    return string.Join(", ", ((System.Collections.IList)list!).Cast<object?>().Select(PythonRepr.Str));
                }
                catch (ArgumentException)
                {
                    // except Exception: text = None (a value the port has no str() for).
                    return null;
                }

            default:
                return null;
        }
    }
    private static long ClampToLong(System.Numerics.BigInteger value) => value > long.MaxValue ? long.MaxValue : (long)value;
}
