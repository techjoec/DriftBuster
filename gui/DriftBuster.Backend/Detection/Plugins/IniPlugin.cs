using System.Text.RegularExpressions;

using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Detection.Plugins;

/// <summary>
/// Detects INI and relatives: sectioned and sectionless INI, Java properties, dotenv, directive-style Unix conf (Apache,
/// nginx) and INI/JSON hybrids.
/// </summary>
/// <remarks>
/// Regexes spell <c>\s</c> as <c>[\s\x1c-\x1f]</c>; case-insensitive keywords and <c>\b</c> are matched by hand. The section
/// and assignment patterns use <c>\G</c> through <see cref="LineStartMatcher"/>, since <c>^\s*</c> searched directly is
/// quadratic over blank-line runs.
/// </remarks>
public sealed partial class IniPlugin : IFormatPlugin
{
    private const int MaxSectionSnapshot = 10;
    private const int ReviewLineWindow = 1000;
    private const int DirectiveCountCap = 10;
    private const int BlockLookahead = 20;
    private const string EngineSpace = @"[\s\x1c-\x1f]";

    private static readonly string[] DefaultIniExtensions = [".ini", ".cfg", ".cnf", ".conf", ".properties", ".env"];

    private static readonly HashSet<string> DotenvFilenames = new(StringComparer.Ordinal)
    {
        ".env",
        ".env.local",
        ".env.development",
        ".env.production",
        ".env.test",
        ".env.example",
        ".env.sample",
    };

    private static readonly string[] DirectiveKeywords = ["include", "loadmodule", "setenv", "option", "alias"];
    private static readonly string[] ApacheKeywords = ["loadmodule", "setenv", "<virtualhost", "<directory", "servername"];

    internal static readonly Regex SectionPattern = new(
        @"\G" + EngineSpace + @"*\[(?<name>[^\]\n]+)\]" + EngineSpace + "*$",
        RegexOptions.Multiline | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));

    internal static readonly Regex KeyValuePattern = new(
        @"\G" + EngineSpace + "*(?<export>export" + EngineSpace + @"+)?(?<key>[A-Za-z0-9_.\-]+)" + EngineSpace
        + "*(?<separator>=|:)" + EngineSpace + @"*(?<value>.*?)(?<continued>\\" + EngineSpace + "*)?$",
        RegexOptions.Multiline | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));

    private static readonly Regex InlineCommentPattern = new(
        EngineSpace + "(?<marker>[;#!])",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));

    private static readonly Regex JsonLikeBracePattern = new(
        @"\{" + EngineSpace + @"*""[^""]+""" + EngineSpace + "*:",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));

    private static readonly Regex InlineJsonAssignmentPattern = new(
        "=" + EngineSpace + @"*\{[^{}]*""[^""]+""" + EngineSpace + "*:",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));

    private static readonly Regex AssignOpenBracePattern = new(
        "=" + EngineSpace + @"*\{",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));

    private static readonly Regex QuotedKeyColonPattern = new(
        @"""[^""\n]+""" + EngineSpace + "*:",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));

    public string Name => "ini";

    public int Priority => 170;

    public string Version => "0.0.2";

    /// <summary>Extensions that count as an INI hint (test seam).</summary>
    internal HashSet<string> IniExtensions { get; set; } = new(DefaultIniExtensions, StringComparer.Ordinal);

    public DetectionMatch? Detect(string path, byte[] sample, string? text)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(sample);
        if (text is null)
        {
            return null;
        }

        var scan = new Scan(PathText.NameLower(path), PathText.SuffixLower(path), text);
        var reasons = new List<string>();
        var metadata = new OrderedDictionary<string, object?>(StringComparer.Ordinal);
        var reviewReasons = new List<string>();

        RecordEncoding(sample, reasons, metadata);
        CollectSections(scan, reasons, metadata);
        CollectKeyValues(scan, reasons, metadata);
        CollectLines(scan);
        CollectCommentStyle(scan, reasons, metadata);
        CollectSensitiveHints(scan, reasons, metadata);
        CollectNameAndDirectiveHints(scan, reasons, metadata);
        var signals = BuildSignals(scan);
        CollectReviewReasons(scan, reviewReasons);

        var effectiveLines = Math.Max(scan.NonEmptyLines.Count - scan.CommentLines.Count, 1);
        scan.KeyDensity = (double)scan.KeyPairCount / effectiveLines;
        metadata["key_density"] = EngineRound(scan.KeyDensity, 3);

        if (!PassesGates(scan))
        {
            return null;
        }

        scan.SignalScore = ComputeSignalScore(scan);

        var confidence = 0.4;
        if (scan.SignalScore < 2)
        {
            if (string.Equals(scan.Extension, ".preferences", StringComparison.Ordinal) && scan.KeyPairCount >= 2)
            {
                reasons.Add("Preferences file with colon assignments treated as INI");
                confidence = Math.Max(confidence, 0.6);
                return new DetectionMatch(Name, "ini", "sectionless-ini", confidence, reasons, metadata);
            }

            // Fallback: commented Java properties exemplars.
            if (!string.Equals(scan.Extension, ".properties", StringComparison.Ordinal) || !HasCommentedPropertyExamples(scan))
            {
                return null;
            }

            reasons.Add("Found numerous commented key/value examples in .properties file");
            confidence = Math.Max(confidence, 0.56);
        }

        confidence = AccumulateConfidence(scan, confidence);
        return Classify(scan, signals, confidence, reasons, metadata, reviewReasons);
    }

    private static bool HasCommentedPropertyExamples(Scan scan)
    {
        var commentedPairs = 0;
        foreach (var line in scan.Lines.Take(ReviewLineWindow))
        {
            var s = EngineText.StripStart(line);
            if (s.StartsWith('#') || s.StartsWith(';'))
            {
                if (s.Contains('=', StringComparison.Ordinal) || s.Contains(':', StringComparison.Ordinal))
                {
                    commentedPairs++;
                }
            }
        }

        return commentedPairs >= 10;
    }

    private static double AccumulateConfidence(Scan scan, double confidence)
    {
        confidence += Math.Min(scan.KeyDensity, 0.6) * 0.25;
        if (scan.Sections.Count > 0)
        {
            confidence += 0.2;
        }

        if (scan.ExtensionHint)
        {
            confidence += 0.1;
        }

        if (scan.DotenvHint)
        {
            confidence += 0.05;
        }

        if (scan.DirectiveSignal)
        {
            confidence += 0.1;
        }

        if (scan.ExportLines > 0)
        {
            confidence += 0.08;
        }

        if (scan.CommentSignal)
        {
            confidence += 0.05;
        }

        if (scan.KeyPairCount >= 6)
        {
            confidence += 0.05;
        }

        return Math.Min(confidence, 0.95);
    }
}
