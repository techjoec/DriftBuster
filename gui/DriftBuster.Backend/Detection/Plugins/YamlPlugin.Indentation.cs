using System.Text.Json.Nodes;

using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Json;

namespace DriftBuster.Backend.Detection.Plugins;

/// <summary>Coarse indentation profile: style, baseline width, tolerated widths and the lines that drift from them.</summary>
public sealed partial class YamlPlugin
{
    private const int IndentLineCap = 10;

    private sealed class IndentTally
    {
        // Insertion-ordered so ties on the most common width go to the first seen.
        public OrderedDictionary<int, int> Stats { get; } = [];

        public OrderedDictionary<int, List<int>> SpaceLines { get; } = [];

        public List<int> TabLines { get; } = [];

        public List<int> MixedLines { get; } = [];
    }

    private static IndentTally TallyIndentation(IReadOnlyList<string> lines)
    {
        var tally = new IndentTally();
        for (var index = 0; index < lines.Count; index++)
        {
            var raw = lines[index];
            var lineNumber = index + 1;
            var stripped = raw.TrimStart();
            if (stripped.Length == 0 || stripped[0] == '#')
            {
                continue;
            }

            var leading = raw.Length - stripped.Length;
            if (leading == 0)
            {
                continue;
            }

            var prefix = raw[..leading];
            var hasTab = prefix.Contains('\t', StringComparison.Ordinal);
            if (hasTab && prefix.Replace("\t", string.Empty, StringComparison.Ordinal).Length > 0)
            {
                tally.MixedLines.Add(lineNumber);
                continue;
            }

            if (hasTab)
            {
                tally.TabLines.Add(lineNumber);
                continue;
            }

            tally.Stats[leading] = tally.Stats.TryGetValue(leading, out var count) ? count + 1 : 1;
            if (!tally.SpaceLines.TryGetValue(leading, out var occurrences))
            {
                occurrences = [];
                tally.SpaceLines[leading] = occurrences;
            }

            occurrences.Add(lineNumber);
        }

        return tally;
    }

    private static JsonObject? AnalyseIndentation(IReadOnlyList<string> lines)
    {
        var tally = TallyIndentation(lines);
        if (tally.Stats.Count == 0 && tally.TabLines.Count == 0 && tally.MixedLines.Count == 0)
        {
            return null;
        }

        var metadata = new JsonObject();
        if (tally.TabLines.Count > 0 && tally.Stats.Count == 0)
        {
            metadata["style"] = "tabs";
            metadata["tab_lines"] = JsonNodes.Numbers(tally.TabLines.Take(IndentLineCap).ToList());
            return metadata;
        }

        metadata["style"] = tally.Stats.Count > 0 ? "spaces" : "mixed";
        if (tally.Stats.Count > 0)
        {
            AddSpaceProfile(metadata, tally);
        }

        if (tally.TabLines.Count > 0)
        {
            metadata["tab_lines"] = JsonNodes.Numbers(tally.TabLines.Take(IndentLineCap).ToList());
            metadata["style"] = tally.Stats.Count > 0 ? "mixed" : "tabs";
        }

        if (tally.MixedLines.Count > 0)
        {
            metadata["mixed_indent_lines"] = JsonNodes.Numbers(tally.MixedLines.Take(IndentLineCap).ToList());
            metadata["style"] = "mixed";
        }

        return metadata.Count > 0 ? metadata : null;
    }

    private static void AddSpaceProfile(JsonObject metadata, IndentTally tally)
    {
        var baseline = 0;
        var best = -1;
        foreach (var (width, count) in tally.Stats)
        {
            if (count > best)
            {
                best = count;
                baseline = width;
            }
        }

        // Allow progressive multiples (2x, 3x) and off-by-two for nested structures that add extra padding.
        var allowed = new HashSet<int> { baseline };
        foreach (var width in tally.Stats.Keys)
        {
            if (width % baseline == 0 || Math.Abs(width - baseline) <= 2)
            {
                allowed.Add(width);
            }
        }

        var outliers = new List<int>();
        foreach (var (width, occurrences) in tally.SpaceLines)
        {
            if (!allowed.Contains(width))
            {
                outliers.AddRange(occurrences.Take(IndentLineCap));
            }
        }

        metadata["baseline"] = baseline;
        metadata["allowed_widths"] = JsonNodes.Numbers(allowed.Order().ToList());
        if (outliers.Count > 0)
        {
            metadata["outlier_lines"] = JsonNodes.Numbers(outliers.Distinct().Order().ToList());
        }
    }
}
