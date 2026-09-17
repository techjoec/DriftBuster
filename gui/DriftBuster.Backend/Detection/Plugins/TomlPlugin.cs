using System.Text.RegularExpressions;

using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Detection.Plugins;

/// <summary>
/// Heuristic TOML detector without a TOML parser: <c>.toml</c> extension hint, <c>[[array.of.tables]]</c>,
/// <c>[table]</c> headers, <c>key = value</c> pairs, quoted, array and inline-table values.
/// </summary>
/// <remarks>
/// Regexes here are the rule patterns with <c>\s</c> spelled <c>[\s\x1c-\x1f]</c>: .NET's <c>\s</c> is
/// <c>[\f\n\r\t\v\x85\p{Z}]</c>, which is Python's <c>str.isspace</c> set minus U+001C-U+001F. The three
/// <c>^\s*</c> MULTILINE patterns are spelled with <c>\G</c> and driven from every line start by
/// <see cref="LineStartMatcher"/> (linear over blank-line runs). Every other construct
/// used has the same meaning in both engines for every input: the character classes are ASCII, <c>$</c>
/// under Multiline matches before <c>\n</c> only, <c>.</c> excludes <c>\n</c> unless Singleline (DOTALL), and
/// the negated classes and lazy quantifiers see an astral character as two units where Python sees one code point,
/// which changes neither match existence nor match boundaries because none of those quantifiers has a minimum
/// above one unit that a single code point could fail. Match counts are non-overlapping in both engines.
/// </remarks>
public sealed partial class TomlPlugin : IFormatPlugin
{
    private const string Extension = ".toml";
    private const int BareKeyLineWindow = 500;
    private const int BareKeyThreshold = 3;
    private const string EngineSpace = @"[\s\x1c-\x1f]";

    internal static readonly Regex TableHeaderPattern = new(
        @"\G" + EngineSpace + @"*\[[A-Za-z0-9_.\-]+\]" + EngineSpace + "*$",
        RegexOptions.Multiline | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));

    internal static readonly Regex ArrayOfTablesPattern = new(
        @"\G" + EngineSpace + @"*\[\[[A-Za-z0-9_.\-]+\]\]" + EngineSpace + "*$",
        RegexOptions.Multiline | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));

    internal static readonly Regex KeyEqualsPattern = new(
        @"\G" + EngineSpace + @"*[A-Za-z0-9_.\-]+" + EngineSpace + "*=" + EngineSpace + "*.+$",
        RegexOptions.Multiline | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));

    private static readonly Regex QuotedValuePattern = new(
        "=" + EngineSpace + @"*(?:""[^""]*""|'[^']*')",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));

    private static readonly Regex ArrayValuePattern = new(
        "=" + EngineSpace + @"*\[.*?\]",
        RegexOptions.Singleline | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));

    private static readonly Regex InlineTablePattern = new(
        "=" + EngineSpace + @"*\{.*?\}",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));

    private static readonly Regex TrailingCommaPattern = new(
        "," + EngineSpace + @"*\]",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));

    public string Name => "toml";

    public int Priority => 165;

    public string Version => "0.0.3";

    private sealed record Signals(
        bool HasArrayTables,
        bool HasTableHeaders,
        bool HasKeyEquals,
        int QuotedPairs,
        int ArrayPairs,
        int InlineTables)
    {
        public int ContentSignalCount => new[]
        {
            HasArrayTables, HasTableHeaders, HasKeyEquals, QuotedPairs > 0, ArrayPairs > 0, InlineTables > 0,
        }.Count(flag => flag);
    }

    public DetectionMatch? Detect(string path, byte[] sample, string? text)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (text is null)
        {
            return null;
        }

        var isTomlExtension = string.Equals(PathText.SuffixLower(path), Extension, StringComparison.Ordinal);
        var reasons = new List<string>();
        if (isTomlExtension)
        {
            reasons.Add("File extension .toml suggests TOML content");
        }

        var signals = new Signals(
            LineStartMatcher.IsMatch(ArrayOfTablesPattern, text),
            LineStartMatcher.IsMatch(TableHeaderPattern, text),
            LineStartMatcher.IsMatch(KeyEqualsPattern, text),
            QuotedValuePattern.Count(text),
            ArrayValuePattern.Count(text),
            InlineTablePattern.Count(text));

        AddSignalReasons(reasons, signals);

        // Gate on content signals only; treat extension as a confidence hint, not a gate.
        if (signals.ContentSignalCount < 2)
        {
            return null;
        }

        return BuildMatch(text, isTomlExtension, signals, reasons);
    }

    private static void AddSignalReasons(List<string> reasons, Signals signals)
    {
        if (signals.HasArrayTables)
        {
            reasons.Add("Found [[array-of-tables]] declaration");
        }

        if (signals.HasTableHeaders)
        {
            reasons.Add("Found [table] headers typical of TOML");
        }

        if (signals.HasKeyEquals)
        {
            reasons.Add("Detected key = value assignments");
        }

        if (signals.QuotedPairs > 0)
        {
            reasons.Add("Found quoted value assignments");
        }

        if (signals.ArrayPairs > 0)
        {
            reasons.Add("Found array value assignments");
        }

        if (signals.InlineTables > 0)
        {
            reasons.Add("Found inline table assignments");
        }
    }

    private DetectionMatch BuildMatch(string text, bool isTomlExtension, Signals signals, List<string> reasons)
    {
        var variant = signals.HasArrayTables ? "array-of-tables" : "generic";
        var metadata = new OrderedDictionary<string, object?>(StringComparer.Ordinal);
        var reviewReasons = new List<string>();

        // Oddities: suspect trailing commas in arrays or lines missing '=' where expected.
        if (TrailingCommaPattern.IsMatch(text))
        {
            reviewReasons.Add("Array with trailing comma before closing bracket");
        }

        var lines = TextLines.SplitLines(text);
        if (CountBareKeyLines(lines) >= BareKeyThreshold && !signals.HasTableHeaders)
        {
            reviewReasons.Add("Multiple bare key lines without '=' suggest malformed TOML");
        }

        var spacingProfile = AnalyseSpacing(lines);
        if (spacingProfile is not null)
        {
            metadata["key_value_spacing"] = spacingProfile;
            if (spacingProfile.ContainsKey("tab_lines"))
            {
                reviewReasons.Add("Tab characters around '=' detected in TOML sample");
            }
        }

        var confidence = Confidence(isTomlExtension, signals);

        if (reviewReasons.Count > 0)
        {
            metadata["needs_review"] = true;
            metadata["review_reasons"] = reviewReasons;
        }

        return new DetectionMatch(
            Name,
            "toml",
            variant,
            confidence,
            reasons.Count > 0 ? reasons : ["Heuristics indicate TOML"],
            metadata.Count > 0 ? metadata : null);
    }

    // Extension contributes as a hint only.
    private static double Confidence(bool isTomlExtension, Signals signals)
    {
        var confidence = 0.5;
        if (isTomlExtension)
        {
            confidence += 0.12;
        }

        if (signals.HasArrayTables)
        {
            confidence += 0.15;
        }

        if (signals.HasTableHeaders)
        {
            confidence += 0.1;
        }

        if (signals.HasKeyEquals)
        {
            confidence += 0.05;
        }

        if (signals.QuotedPairs > 0 || signals.ArrayPairs > 0)
        {
            confidence += 0.05;
        }

        if (signals.InlineTables > 0)
        {
            confidence += 0.03;
        }

        return Math.Min(0.95, confidence);
    }

    // Lines that look like bare keys without '=' (risky, keep conservative): first 500 lines only.
    private static int CountBareKeyLines(IReadOnlyList<string> lines)
    {
        var count = 0;
        foreach (var line in lines.Take(BareKeyLineWindow))
        {
            var stripped = EngineText.StripStart(line);
            if (EngineText.Strip(line).Length == 0
                || stripped[0] is '#' or ';' or '['
                || line.Contains('=', StringComparison.Ordinal)
                || line.Contains(':', StringComparison.Ordinal))
            {
                continue;
            }

            count++;
        }

        return count;
    }
}
