using System.Text.Json.Nodes;

using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Json;

namespace DriftBuster.Backend.Detection.Plugins;

/// <summary>Profile of the spaces on each side of '=' in assignment lines, plus lines that use tabs around it.</summary>
public sealed partial class TomlPlugin
{
    private const int SpacingTabLineCap = 10;

    private static int TrailingSpaceCount(string text)
    {
        var count = 0;
        for (var index = text.Length - 1; index >= 0 && text[index] == ' '; index--)
        {
            count++;
        }

        return count;
    }

    private static int LeadingSpaceCount(string text)
    {
        var count = 0;
        while (count < text.Length && text[count] == ' ')
        {
            count++;
        }

        return count;
    }

    private static void Increment(OrderedDictionary<int, int> counter, int key)
        => counter[key] = counter.TryGetValue(key, out var count) ? count + 1 : 1;

    // Counter.most_common(1): the largest count, ties resolved to the first key inserted.
    private static int MostCommon(OrderedDictionary<int, int> counter)
    {
        var best = -1;
        var result = 0;
        foreach (var (key, count) in counter)
        {
            if (count > best)
            {
                best = count;
                result = key;
            }
        }

        return result;
    }

    private static List<int> AllowedAround(int baseline)
        => new HashSet<int> { baseline, Math.Max(0, baseline - 1), baseline + 1 }.Order().ToList();

    private static JsonObject? AnalyseSpacing(IReadOnlyList<string> lines)
    {
        var beforeCounter = new OrderedDictionary<int, int>();
        var afterCounter = new OrderedDictionary<int, int>();
        var tabLines = new List<int>();

        for (var index = 0; index < lines.Count; index++)
        {
            var raw = lines[index];
            var separator = raw.IndexOf('=', StringComparison.Ordinal);
            if (separator < 0)
            {
                continue;
            }

            var stripped = EngineText.StripStart(raw);
            if (stripped.Length == 0 || stripped[0] is '#' or ';' or '[')
            {
                continue;
            }

            var left = raw[..separator];
            var right = raw[(separator + 1)..];
            if (left.Contains('\t', StringComparison.Ordinal) || right.Contains('\t', StringComparison.Ordinal))
            {
                tabLines.Add(index + 1);
            }

            Increment(beforeCounter, TrailingSpaceCount(left));
            Increment(afterCounter, LeadingSpaceCount(right));
        }

        if (beforeCounter.Count == 0 && afterCounter.Count == 0 && tabLines.Count == 0)
        {
            return null;
        }

        var metadata = new JsonObject();
        if (beforeCounter.Count > 0)
        {
            var beforeBase = MostCommon(beforeCounter);
            metadata["before"] = beforeBase;
            metadata["allowed_before"] = JsonNodes.Numbers(AllowedAround(beforeBase));
        }

        if (afterCounter.Count > 0)
        {
            var afterBase = MostCommon(afterCounter);
            metadata["after"] = afterBase;
            metadata["allowed_after"] = JsonNodes.Numbers(AllowedAround(afterBase));
        }

        if (tabLines.Count > 0)
        {
            metadata["tab_lines"] = JsonNodes.Numbers(tabLines.Take(SpacingTabLineCap).ToList());
        }

        return metadata.Count > 0 ? metadata : null;
    }
}
