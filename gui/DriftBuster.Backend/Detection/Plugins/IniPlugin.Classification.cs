using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Detection.Plugins;

/// <summary>Brace/JSON-hybrid analysis and the format/variant ladder.</summary>
public sealed partial class IniPlugin
{
    private static bool HasBraceSignal(Scan scan)
    {
        var braceLines = scan.NonEmptyLines
            .Where(line => line.Contains('{', StringComparison.Ordinal) || line.Contains('}', StringComparison.Ordinal))
            .ToList();
        if (braceLines.Count == 0)
        {
            return false;
        }

        var standaloneBraceLine = braceLines.Any(IsStandaloneBraceLine);
        var jsonLikeBraceLine = braceLines.Any(line => JsonLikeBracePattern.IsMatch(line));
        var structuredBraceLines = braceLines.Count(line => line.Contains(':', StringComparison.Ordinal));
        var braceTokenCount = braceLines.Sum(line => line.AsSpan().Count('{') + line.AsSpan().Count('}'));

        var inlineJsonAssignment = scan.NonEmptyLines.Any(line =>
            line.Contains('}', StringComparison.Ordinal) && InlineJsonAssignmentPattern.IsMatch(line));

        return standaloneBraceLine
            || jsonLikeBraceLine
            || inlineJsonAssignment
            || HasBlockJsonAssignment(scan.NonEmptyLines)
            || (structuredBraceLines >= 2 && braceTokenCount >= 4);
    }

    // An "= {" opening on one line closed by a "}" within the next nineteen non-empty lines, with at least one quoted
    // key followed by ':' along the way; a section header or another "{ ... =" assignment ends the look-ahead.
    private static bool HasBlockJsonAssignment(List<string> nonEmptyLines)
    {
        for (var idx = 0; idx < nonEmptyLines.Count; idx++)
        {
            var line = nonEmptyLines[idx];
            if (!line.Contains('{', StringComparison.Ordinal) || !line.Contains('=', StringComparison.Ordinal))
            {
                continue;
            }

            if (!AssignOpenBracePattern.IsMatch(line))
            {
                continue;
            }

            var afterBrace = line[(line.IndexOf('{', StringComparison.Ordinal) + 1)..];
            var colonHits = QuotedKeyColonPattern.Matches(afterBrace).Count;
            var closingFound = line.Contains('}', StringComparison.Ordinal);

            var limit = Math.Min(nonEmptyLines.Count - idx, BlockLookahead);
            for (var offset = 1; offset < limit; offset++)
            {
                var candidate = nonEmptyLines[idx + offset];
                if (SectionPattern.IsMatch(candidate))
                {
                    break;
                }

                colonHits += QuotedKeyColonPattern.Matches(candidate).Count;
                if (candidate.Contains('}', StringComparison.Ordinal))
                {
                    closingFound = true;
                    break;
                }

                if (candidate.Contains('{', StringComparison.Ordinal) && candidate.Contains('=', StringComparison.Ordinal))
                {
                    break;
                }
            }

            if (closingFound && colonHits > 0)
            {
                return true;
            }
        }

        return false;
    }

    private sealed class Classification
    {
        public string FormatName { get; set; } = "ini";

        public string? Variant { get; set; }

        public List<string> Reasons { get; } = [];
    }

    private DetectionMatch? Classify(
        Scan scan,
        OrderedDictionary<string, object?> signals,
        double confidence,
        List<string> reasons,
        OrderedDictionary<string, object?> metadata,
        List<string> reviewReasons)
    {
        var classification = ClassifyLadder(scan);
        if (classification is null)
        {
            return null;
        }

        if (string.Equals(scan.LowerName, "desktop.ini", StringComparison.Ordinal))
        {
            classification.Variant = "desktop-ini";
            classification.Reasons.Add("Recognized Windows desktop.ini profile file name");
        }

        reasons.AddRange(classification.Reasons);

        metadata["detector_lineage"] = new OrderedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["family"] = "ini-lineage",
            ["format"] = classification.FormatName,
            ["variant"] = classification.Variant ?? "unspecified",
            ["signal_score"] = scan.SignalScore,
            ["signals"] = signals,
        };

        if (string.Equals(classification.FormatName, "env-file", StringComparison.Ordinal))
        {
            AppendEnvRemediation(metadata);
        }

        if (reviewReasons.Count > 0)
        {
            metadata["needs_review"] = true;
            metadata["review_reasons"] = reviewReasons;
        }

        return new DetectionMatch(Name, classification.FormatName, classification.Variant, confidence, reasons, metadata);
    }

    private static Classification? ClassifyLadder(Scan scan)
    {
        var hasSections = scan.Sections.Count > 0;
        var isYaml = scan.Extension is ".yml" or ".yaml";
        var isProperties = string.Equals(scan.Extension, ".properties", StringComparison.Ordinal);
        var directiveDensity = scan.NonEmptyLines.Count > 0
            ? (double)scan.DirectiveLines.Count / Math.Max(scan.NonEmptyLines.Count, 1)
            : 0.0;
        var envStyle = !hasSections
            && scan.KeyPairCount > 0
            && (scan.EqualsPairs > 0 || scan.ExportLines > 0)
            && !scan.DirectiveSignal
            && !isProperties;

        var result = new Classification();
        if (hasSections && HasBraceSignal(scan))
        {
            result.FormatName = "ini-json-hybrid";
            result.Variant = "section-json-hybrid";
            result.Reasons.Add("Detected JSON-style braces alongside [section] headers indicating hybrid structure");
        }
        else if (envStyle)
        {
            result.FormatName = "env-file";
            result.Variant = "dotenv";
            result.Reasons.Add("Sectionless KEY=VALUE or export assignments resemble dotenv env files");
        }
        else if (scan.DirectiveSignal && (!hasSections || scan.DirectiveLines.Count >= 2 || directiveDensity >= 0.3))
        {
            // Avoid stealing YAML payloads; let the YAML plugin classify .yml/.yaml.
            if (isYaml)
            {
                return null;
            }

            ClassifyDirectiveConf(scan.Text, result);
        }
        else if (hasSections)
        {
            result.Variant = "sectioned-ini";
            result.Reasons.Add("Section headers confirm classic INI layout");
        }
        else if (isProperties)
        {
            result.Variant = "java-properties";
            result.Reasons.Add("File extension .properties with key/value pairs suggests Java properties");
        }
        else
        {
            // Avoid env-file misclassification for YAML.
            if (isYaml)
            {
                return null;
            }

            result.Variant = "sectionless-ini";
            result.Reasons.Add("Key/value pairs without sections default to sectionless INI interpretation");
        }

        return result;
    }

    private static void ClassifyDirectiveConf(string text, Classification result)
    {
        result.FormatName = "unix-conf";
        result.Variant = "directive-conf";
        result.Reasons.Add("Directive-heavy configuration without sections classified as Unix-style conf");

        if (HasApacheHint(text))
        {
            result.Variant = "apache-conf";
            result.Reasons.Add("Matched Apache directive keywords such as LoadModule/SetEnv");
        }
        else if (HasNginxHint(text))
        {
            result.Variant = "nginx-conf";
            result.Reasons.Add("Detected nginx-style server/location blocks");
        }
    }

    private static void AppendEnvRemediation(OrderedDictionary<string, object?> metadata)
    {
        var existing = new List<OrderedDictionary<string, object?>>();
        if (metadata.TryGetValue("remediations", out var current) && current is List<OrderedDictionary<string, object?>> entries)
        {
            existing.AddRange(entries);
        }

        var envEntry = new OrderedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = "env-sanitisation-workflow",
            ["category"] = "handling",
            ["summary"] = "Sanitise dotenv fixtures via scripts/fixtures/README.md before sharing samples.",
            ["documentation"] = "scripts/fixtures/README.md",
        };
        if (!existing.Any(entry => SameEntries(entry, envEntry)))
        {
            existing.Add(envEntry);
        }

        metadata["remediations"] = existing;
    }

    // Python dict equality: the same keys with equal values.
    private static bool SameEntries(OrderedDictionary<string, object?> left, OrderedDictionary<string, object?> right)
        => left.Count == right.Count
            && left.All(pair => right.TryGetValue(pair.Key, out var value) && Equals(pair.Value, value));
}
