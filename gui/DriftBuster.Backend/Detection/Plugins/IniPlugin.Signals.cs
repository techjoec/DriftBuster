using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Json;

namespace DriftBuster.Backend.Detection.Plugins;

/// <summary>Signal collection: encoding, sections, assignments, comments, filename hints and gating.</summary>
public sealed partial class IniPlugin
{
    private static readonly byte[] BomUtf8 = [0xEF, 0xBB, 0xBF];
    private static readonly byte[] BomUtf16Le = [0xFF, 0xFE];
    private static readonly byte[] BomUtf16Be = [0xFE, 0xFF];

    /// <summary>Everything computed from one sample, shared by the collection and classification steps.</summary>
    private sealed class Scan(string lowerName, string extension, string text)
    {
        public string LowerName { get; } = lowerName;

        public string Extension { get; } = extension;

        public string Text { get; } = text;

        public List<string> Sections { get; } = [];

        public List<Match> KeyMatches { get; } = [];

        public int KeyPairCount => KeyMatches.Count;

        public int EqualsPairs { get; set; }

        public int ColonPairs { get; set; }

        public IReadOnlyList<string> Lines { get; set; } = [];

        public List<string> NonEmptyLines { get; } = [];

        public List<string> CommentLines { get; } = [];

        public List<string> DirectiveLines { get; } = [];

        public int ContinuationLines { get; set; }

        public int ExportLines { get; set; }

        public bool DirectiveSignal => DirectiveLines.Count > 0;

        public bool CommentSignal => CommentLines.Count > 0;

        public bool DotenvHint { get; set; }

        public bool ExtensionHint { get; set; }

        public double KeyDensity { get; set; }

        public bool KeyDensityStrong => KeyDensity >= 0.3 || KeyPairCount >= 4;

        public int SignalScore { get; set; }
    }

    private static void RecordEncoding(byte[] sample, List<string> reasons, JsonObject metadata)
    {
        string? detectedCodec = null;
        var bomPresent = false;
        foreach (var (bom, codecName) in new[] { (BomUtf8, "utf-8-sig"), (BomUtf16Le, "utf-16-le"), (BomUtf16Be, "utf-16-be") })
        {
            if (sample.AsSpan().StartsWith(bom))
            {
                bomPresent = true;
                detectedCodec = codecName;
                break;
            }
        }

        detectedCodec ??= FormatRegistry.DecodeText(sample).Encoding;

        metadata["encoding_info"] = new JsonObject()
        {
            ["codec"] = detectedCodec,
            ["bom_present"] = bomPresent,
        };
        metadata.TryAdd("encoding", detectedCodec);
        reasons.Add($"Detected {detectedCodec} codec{(bomPresent ? " with BOM" : string.Empty)}");
    }

    private static void CollectSections(Scan scan, List<string> reasons, JsonObject metadata)
    {
        foreach (var match in LineStartMatcher.Matches(SectionPattern, scan.Text))
        {
            var section = match.Groups["name"].Value.Trim();
            if (section.Length > 0)
            {
                scan.Sections.Add(section);
            }
        }

        if (scan.Sections.Count == 0)
        {
            return;
        }

        reasons.Add("Found [section] headers indicative of INI structure");
        var uniqueSections = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var section in scan.Sections)
        {
            if (seen.Add(section.ToLowerInvariant()))
            {
                uniqueSections.Add(section);
            }
        }

        metadata["sections"] = JsonNodes.Strings(uniqueSections.Take(MaxSectionSnapshot).ToList());
        metadata["section_count"] = uniqueSections.Count;
    }

    private static void CollectKeyValues(Scan scan, List<string> reasons, JsonObject metadata)
    {
        scan.KeyMatches.AddRange(LineStartMatcher.Matches(KeyValuePattern, scan.Text));

        if (scan.KeyPairCount > 0)
        {
            reasons.Add("Detected key/value assignments typical of INI-style configuration");
            metadata["key_value_pairs"] = scan.KeyPairCount;
        }

        scan.EqualsPairs = scan.KeyMatches.Count(match => string.Equals(match.Groups["separator"].Value, "=", StringComparison.Ordinal));
        scan.ColonPairs = scan.KeyPairCount - scan.EqualsPairs;
        if (scan.EqualsPairs > 0)
        {
            metadata["equals_separator_pairs"] = scan.EqualsPairs;
        }

        if (scan.ColonPairs > 0)
        {
            metadata["colon_separator_pairs"] = scan.ColonPairs;
        }
    }

    private static void CollectLines(Scan scan)
    {
        scan.Lines = TextLines.SplitLines(scan.Text);
        foreach (var line in scan.Lines)
        {
            if (line.Trim().Length == 0)
            {
                continue;
            }

            scan.NonEmptyLines.Add(line);
            if (IsCommentLine(line))
            {
                scan.CommentLines.Add(line);
            }

            if (IsDirectiveLine(line))
            {
                scan.DirectiveLines.Add(line);
            }
        }

        scan.ContinuationLines = scan.KeyMatches.Count(match => match.Groups["continued"].Success);
        scan.ExportLines = scan.KeyMatches.Count(match => match.Groups["export"].Success);
    }

    private static void CollectCommentStyle(Scan scan, List<string> reasons, JsonObject metadata)
    {
        var commentMarkers = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var line in scan.CommentLines)
        {
            var marker = line.TrimStart()[0];
            if (marker is ';' or '#' or '!')
            {
                commentMarkers.Add(marker.ToString());
            }
        }

        var inlineCommentMarkers = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var match in scan.KeyMatches)
        {
            var value = match.Groups["value"].Value;
            if (value.Length == 0)
            {
                continue;
            }

            foreach (Match inlineMatch in InlineCommentPattern.Matches(value))
            {
                inlineCommentMarkers.Add(inlineMatch.Groups["marker"].Value);
            }
        }

        if (scan.ExportLines > 0)
        {
            metadata["export_assignments"] = scan.ExportLines;
            reasons.Add("Detected environment-style export assignments");
        }

        if (scan.ContinuationLines > 0)
        {
            metadata["continuations"] = scan.ContinuationLines;
        }

        var supportsInlineComments = inlineCommentMarkers.Count > 0;
        commentMarkers.UnionWith(inlineCommentMarkers);
        metadata["comment_style"] = new JsonObject()
        {
            ["markers"] = JsonNodes.Strings(commentMarkers.ToList()),
            ["supports_inline_comments"] = supportsInlineComments,
            ["uses_export_prefix"] = scan.ExportLines > 0,
        };
        if (supportsInlineComments)
        {
            reasons.Add("Found inline comment markers following assignments");
        }
    }

    private void CollectNameAndDirectiveHints(Scan scan, List<string> reasons, JsonObject metadata)
    {
        if (scan.DirectiveSignal)
        {
            metadata["directive_line_count"] = Math.Min(scan.DirectiveLines.Count, DirectiveCountCap);
            reasons.Add("Found directive keywords common in INI/conf files");
        }

        var registeredExtension = IniExtensions.Contains(scan.Extension);
        if (registeredExtension)
        {
            reasons.Add($"File extension {scan.Extension} suggests INI-like configuration");
        }
        else if (scan.LowerName.EndsWith(".ini", StringComparison.Ordinal))
        {
            reasons.Add("Filename suffix .ini suggests INI content");
        }

        scan.DotenvHint = DotenvFilenames.Contains(scan.LowerName);
        if (scan.DotenvHint)
        {
            reasons.Add("Filename is a known dotenv-style configuration");
        }

        if (string.Equals(scan.LowerName, "desktop.ini", StringComparison.Ordinal))
        {
            reasons.Add("Filename desktop.ini is commonly produced by Windows shell metadata");
            metadata["profile_hint"] = "desktop.ini";
        }

        if (scan.CommentSignal)
        {
            reasons.Add("Detected comment markers (;, #, !) used by INI variants");
        }

        scan.ExtensionHint = registeredExtension || scan.LowerName.EndsWith(".ini", StringComparison.Ordinal);
    }

    private static JsonObject BuildSignals(Scan scan) => new()
    {
        ["section_count"] = scan.Sections.Count,
        ["key_value_pairs"] = scan.KeyPairCount,
        ["directive_lines"] = scan.DirectiveLines.Count,
        ["export_assignments"] = scan.ExportLines,
        ["comment_lines"] = scan.CommentLines.Count,
        ["continuations"] = scan.ContinuationLines,
        ["has_sections"] = scan.Sections.Count > 0,
        ["has_directives"] = scan.DirectiveSignal,
        ["has_export_prefix"] = scan.ExportLines > 0,
        ["dotenv_hint"] = scan.DotenvHint,
        ["extension_hint"] = scan.ExtensionHint,
    };

    // Oddities: bad section headers or colon-only assignments without .properties.
    private static void CollectReviewReasons(Scan scan, List<string> reviewReasons)
    {
        if (scan.Lines.Take(ReviewLineWindow).Any(IsMalformedSectionLine))
        {
            reviewReasons.Add("Malformed section header without closing bracket");
        }

        if (scan.ColonPairs > 0 && scan.EqualsPairs == 0 && !string.Equals(scan.Extension, ".properties", StringComparison.Ordinal))
        {
            reviewReasons.Add("Colon-only assignments outside .properties context");
        }
    }

    private static bool PassesGates(Scan scan)
    {
        if (!(scan.KeyPairCount > 0 || scan.DirectiveSignal))
        {
            return false;
        }

        // Require substantive content signals even when an extension hint is present: the extension alone never
        // relaxes gating, only dotenv/export and .preferences allowances do.
        var isPreferences = string.Equals(scan.Extension, ".preferences", StringComparison.Ordinal);
        if (scan.Sections.Count == 0
            && !scan.DirectiveSignal
            && !(scan.KeyPairCount >= 1 || scan.DotenvHint || scan.ExportLines > 0 || isPreferences))
        {
            return false;
        }

        var colonOnlyAssignments = scan.ColonPairs > 0 && scan.EqualsPairs == 0;
        if (colonOnlyAssignments
            && !(scan.Sections.Count > 0 || scan.ExtensionHint || scan.DotenvHint || scan.DirectiveSignal || scan.ExportLines > 0))
        {
            // Allow colon-only preferences files used by some tools.
            return isPreferences;
        }

        return true;
    }

    private static int ComputeSignalScore(Scan scan)
    {
        var signalScore = 0;
        if (scan.Sections.Count > 0)
        {
            signalScore++;
        }

        if (scan.KeyPairCount > 0)
        {
            signalScore++;
        }

        if (scan.KeyDensityStrong)
        {
            signalScore++;
        }

        if (scan.ExtensionHint || scan.DotenvHint)
        {
            signalScore++;
        }

        if (scan.DirectiveSignal)
        {
            signalScore++;
        }

        if (scan.ExportLines > 0)
        {
            signalScore++;
        }

        if (scan.CommentSignal)
        {
            signalScore++;
        }

        return signalScore;
    }
}
