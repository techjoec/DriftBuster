using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Detection.Plugins;

/// <summary>
/// Detects JSON and JSON-with-comments configuration files from filename cues, structural hints (opening token,
/// balanced delimiters, key/value markers) and a bounded parse of the sample. Comments outside string literals surface
/// the <c>jsonc</c> variant; <c>appsettings*.json</c> and ASP.NET configuration keys surface
/// <c>structured-settings-json</c>; everything else is <c>generic</c>.
/// </summary>
/// <remarks>
/// Windows are measured in code points like Python <c>str</c> slicing; every scanner compares against ASCII tokens
/// only, so walking UTF-16 units is otherwise equivalent (a surrogate never equals a quote, slash or brace).
/// </remarks>
public sealed partial class JsonPlugin : IFormatPlugin
{
    internal const int AnalysisWindowClamp = 200_000;
    private const int KeyValueMarkerWindow = 10_000;
    private const int TopLevelKeyLimit = 5;

    private static readonly HashSet<string> StructuredFilenames = new(StringComparer.Ordinal)
    {
        "appsettings.json",
        "appsettings.development.json",
        "appsettings.production.json",
        "appsettings.staging.json",
    };

    public string Name => "json";

    public int Priority => 200;

    public string Version => "0.0.3";

    private sealed class Signals
    {
        public required string Extension { get; init; }

        public required bool IsJsonExtension { get; init; }

        public required string AnalysisText { get; init; }

        public required bool Truncated { get; init; }

        public required char FirstChar { get; init; }

        public bool HasComments { get; set; }

        public bool KeySignal { get; set; }

        public bool Balanced { get; set; }

        public string? StructuredHint { get; set; }

        public bool ParsedViaCommentStrip { get; set; }

        public required ParseResult Parse { get; set; }
    }

    public DetectionMatch? Detect(string path, byte[] sample, string? text)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (text is null)
        {
            return null;
        }

        var lowerName = PathText.NameLower(path);
        var extension = PathText.SuffixLower(path);
        var reasons = new List<string>();
        var metadata = new OrderedDictionary<string, object?>(StringComparer.Ordinal);
        var reviewReasons = new List<string>();

        var isJsonExtension = extension is ".json" or ".jsonc" || lowerName.EndsWith(".json", StringComparison.Ordinal);
        if (isJsonExtension)
        {
            reasons.Add($"File extension {(extension.Length == 0 ? ".json" : extension)} suggests JSON content");
        }

        var (stripped, hadLeadingComments) = StripLeadingComments(text);
        if (stripped.Length == 0)
        {
            return null;
        }

        var (analysisText, truncated) = PrepareAnalysisWindow(stripped);

        var firstChar = analysisText[0];
        if (firstChar is '{' or '[')
        {
            var topLevelType = firstChar == '{' ? "object" : "array";
            metadata["top_level_type"] = topLevelType;
            reasons.Add($"Detected JSON {topLevelType} opening token '{firstChar}'");
        }
        else
        {
            if (!isJsonExtension)
            {
                return null;
            }

            metadata["top_level_type"] = "unknown";
        }

        var signals = new Signals
        {
            Extension = extension,
            IsJsonExtension = isJsonExtension,
            AnalysisText = analysisText,
            Truncated = truncated,
            FirstChar = firstChar,
            HasComments = hadLeadingComments || ContainsComments(analysisText),
            Parse = ParseResult.Failure,
        };
        CollectSignals(signals, lowerName, reasons, metadata);
        AttemptParseInto(signals, reasons, metadata, reviewReasons);

        return ApplyGates(signals, metadata) ? BuildMatch(signals, reasons, metadata, reviewReasons) : null;
    }

    private static void CollectSignals(Signals signals, string lowerName, List<string> reasons, OrderedDictionary<string, object?> metadata)
    {
        var analysisText = signals.AnalysisText;
        if (signals.HasComments)
        {
            metadata["has_comments"] = true;
            reasons.Add("Detected comment tokens outside string literals");
        }

        signals.KeySignal = HasKeyValueMarker(analysisText);
        if (signals.KeySignal)
        {
            reasons.Add("Found key/value signature indicative of JSON objects");
        }

        signals.Balanced = BalancedPairs(analysisText);
        if (signals.Balanced)
        {
            reasons.Add("Curly/array delimiters appear balanced in sampled content");
        }

        signals.StructuredHint = IsStructuredSettings(lowerName, analysisText);
        if (signals.StructuredHint is not null)
        {
            metadata["settings_hint"] = signals.StructuredHint;
            reasons.Add("Matched appsettings-style configuration cues");
        }

        var environment = ExtractAppsettingsEnvironment(lowerName);
        if (environment is not null)
        {
            metadata["settings_environment"] = environment;
        }
    }

    private static void AttemptParseInto(Signals signals, List<string> reasons, OrderedDictionary<string, object?> metadata, List<string> reviewReasons)
    {
        var parseCandidate = signals.AnalysisText;
        if (signals.HasComments)
        {
            var (cleaned, removed) = StripJsonComments(signals.AnalysisText);
            if (removed)
            {
                parseCandidate = cleaned;
                signals.ParsedViaCommentStrip = true;
            }
        }

        var allowCommentsFlag = signals.HasComments && !signals.ParsedViaCommentStrip;
        signals.Parse = AttemptParse(parseCandidate, allowComments: allowCommentsFlag);
        if (signals.Parse.Success)
        {
            foreach (var pair in signals.Parse.Metadata)
            {
                metadata[pair.Key] = pair.Value;
            }

            reasons.Add("Parsed JSON payload without errors within sample");
            if (signals.ParsedViaCommentStrip)
            {
                metadata["parsed_with_comment_stripping"] = true;
            }
        }
        else if ((signals.FirstChar is '[' or '{' || signals.KeySignal || signals.Balanced)
            && !signals.HasComments
            && !signals.Truncated)
        {
            // Parsing failed despite JSON-like signals; flag for review.
            metadata["parse_failed"] = true;
            reviewReasons.Add("JSON parse failed under sample");
        }
    }

    private static bool HasContainerTopLevel(OrderedDictionary<string, object?> metadata)
        => metadata.TryGetValue("top_level_type", out var value) && value is "object" or "array";

    private static bool ApplyGates(Signals signals, OrderedDictionary<string, object?> metadata)
    {
        // Gate detection on content signals only; extension is a confidence hint, not a gate.
        var contentSignals = 0;
        foreach (var flag in new[] { HasContainerTopLevel(metadata), signals.KeySignal, signals.Balanced, signals.Parse.Success })
        {
            if (flag)
            {
                contentSignals++;
            }
        }

        // Allow minimal detection for known JSON extensions even if content signals are weak, to align with
        // real-world appsettings-style files that may be tiny or truncated in samples. (The stripped text is
        // never empty here, so Python's "is_json_extension and stripped" reduces to the extension flag.)
        if (contentSignals < 2 && !signals.IsJsonExtension)
        {
            return false;
        }

        // For non-JSON extensions, require at least a key/value marker or a successful parse to avoid over-eager
        // matches on brace-like text.
        return signals.IsJsonExtension || signals.Parse.Success || signals.KeySignal;
    }

    private DetectionMatch BuildMatch(Signals signals, List<string> reasons, OrderedDictionary<string, object?> metadata, List<string> reviewReasons)
    {
        string variant;
        if (signals.StructuredHint is not null)
        {
            variant = "structured-settings-json";
        }
        else if (signals.HasComments || string.Equals(signals.Extension, ".jsonc", StringComparison.Ordinal))
        {
            variant = "jsonc";
        }
        else
        {
            variant = "generic";
        }

        var confidence = ComputeConfidence(signals, metadata, variant);

        if (signals.Truncated)
        {
            metadata["analysis_window_truncated"] = true;
            metadata["analysis_window_chars"] = CodePointCount(signals.AnalysisText);
        }

        if (reviewReasons.Count > 0)
        {
            metadata["needs_review"] = true;
            metadata["review_reasons"] = reviewReasons;
        }

        return new DetectionMatch(Name, "json", variant, confidence, reasons, metadata);
    }

    private static double ComputeConfidence(Signals signals, OrderedDictionary<string, object?> metadata, string variant)
    {
        var confidence = 0.55;
        // Extension contributes as a hint only.
        if (signals.IsJsonExtension)
        {
            confidence += 0.1;
        }

        if (HasContainerTopLevel(metadata))
        {
            confidence += 0.1;
        }

        if (signals.KeySignal)
        {
            confidence += 0.05;
        }

        if (signals.Balanced)
        {
            confidence += 0.05;
        }

        if (signals.Parse.Success)
        {
            confidence += 0.15;
        }

        if (signals.StructuredHint is not null)
        {
            confidence += 0.07;
        }

        if (signals.HasComments && string.Equals(variant, "jsonc", StringComparison.Ordinal))
        {
            confidence += 0.03;
        }

        if (signals.ParsedViaCommentStrip)
        {
            confidence += 0.02;
        }

        return Math.Min(0.95, confidence);
    }
}
