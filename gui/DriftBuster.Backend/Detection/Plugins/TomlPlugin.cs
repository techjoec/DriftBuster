using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Json;

namespace DriftBuster.Backend.Detection.Plugins;

/// <summary>
/// Heuristic TOML detector without a parser: <c>.toml</c> extension hint, <c>[[array.of.tables]]</c>, <c>[table]</c> headers,
/// <c>key = value</c> pairs, quoted, array and inline-table values.
/// </summary>
/// <remarks>
/// Regexes spell <c>\s</c> as <c>[\s\x1c-\x1f]</c> (the engine's whitespace set); the <c>^\s*</c> multiline patterns use
/// <c>\G</c> and run through <see cref="LineStartMatcher"/> to stay linear over blank-line runs.
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

    public string Version => "0.0.4";

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

        // Headers and bare key=value lines are also sectioned INI; only these are TOML's own.
        public bool HasTomlOnlySignal => HasArrayTables || QuotedPairs > 0 || ArrayPairs > 0 || InlineTables > 0;
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

        // Gate on content signals; the extension is a confidence hint. Without the .toml extension a sectioned
        // key=value file needs a TOML-only construct, so INI files are left to the INI plugin.
        if (signals.ContentSignalCount < 2 || (!isTomlExtension && !signals.HasTomlOnlySignal))
        {
            return null;
        }

        return BuildMatch(path, text, isTomlExtension, signals, reasons);
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

    private static readonly HashSet<string> ManifestTables = new(StringComparer.Ordinal) { "package", "project", "build-system", "tool.poetry" };

    // Tool configuration files named by their tool's documentation.
    private static readonly HashSet<string> SettingsFileNames = new(StringComparer.Ordinal)
    {
        "rustfmt.toml", ".rustfmt.toml", "ruff.toml", ".ruff.toml", "netlify.toml", "taplo.toml", ".taplo.toml",
        "clippy.toml", ".clippy.toml", "deny.toml", "rust-toolchain.toml", "typos.toml", "_typos.toml",
    };

    /// <summary>
    /// <c>package-manifest-toml</c> for Cargo.toml or <c>[package]</c>, <c>[project]</c>, <c>[build-system]</c> or
    /// <c>[tool.poetry…]</c> tables; <c>project-settings-toml</c> for known tool config names, <c>.cargo/config.toml</c> or files
    /// whose tables are all <c>[tool.*]</c>; else <c>array-of-tables</c> or <c>generic</c>.
    /// </summary>
    private static string ChooseVariant(string path, string text, Signals signals, List<string> reasons)
    {
        var tables = TableNames(text);
        var name = PathText.NameLower(path);
        if (string.Equals(name, "cargo.toml", StringComparison.Ordinal)
            || tables.Any(table => ManifestTables.Contains(table) || table.StartsWith("tool.poetry.", StringComparison.Ordinal)))
        {
            reasons.Add("Found package manifest tables or file name");
            return "package-manifest-toml";
        }

        var inCargoFolder = string.Equals(EngineText.Lower(PathText.Name(LexicalPath.Parent(path))), ".cargo", StringComparison.Ordinal);
        var toolTablesOnly = tables.Count > 0 && tables.All(table => table.StartsWith("tool.", StringComparison.Ordinal));
        if (SettingsFileNames.Contains(name) || (inCargoFolder && string.Equals(name, "config.toml", StringComparison.Ordinal)) || toolTablesOnly)
        {
            reasons.Add("Found tool settings tables or file name");
            return "project-settings-toml";
        }

        return signals.HasArrayTables ? "array-of-tables" : "generic";
    }

    // The [table] and [[table]] names, in file order, read line by line.
    private static List<string> TableNames(string text)
    {
        var names = new List<string>();
        foreach (var raw in TextLines.SplitLines(text))
        {
            var line = raw.Trim();
            if (line.Length < 3 || line[0] != '[')
            {
                continue;
            }

            var inner = line.StartsWith("[[", StringComparison.Ordinal) && line.EndsWith("]]", StringComparison.Ordinal)
                ? line[2..^2]
                : line.EndsWith(']') ? line[1..^1] : null;
            if (inner is not null && inner.Trim().Length > 0)
            {
                names.Add(inner.Trim().Trim('"'));
            }
        }

        return names;
    }

    private DetectionMatch BuildMatch(string path, string text, bool isTomlExtension, Signals signals, List<string> reasons)
    {
        var variant = ChooseVariant(path, text, signals, reasons);
        var metadata = new JsonObject();
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
            metadata["review_reasons"] = JsonNodes.Strings(reviewReasons);
        }

        return new DetectionMatch(
            Name,
            "toml",
            variant,
            confidence,
            reasons.Count > 0 ? reasons : ["Heuristics indicate TOML"],
            metadata);
    }

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
