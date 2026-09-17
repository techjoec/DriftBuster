using DriftBuster.Backend.Detection;
using DriftBuster.Backend.Detection.Catalog;
using DriftBuster.Backend.Diff;
using DriftBuster.Backend.Hunt;
using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Tests.Reporting;

/// <summary>
/// CPython oracle data written by <c>tools/parity/gen_report_cases.py</c>, read with <see cref="PythonJson"/> so unpaired surrogate
/// escapes and NaN decode exactly as Python wrote them, and the case inputs rebuilt as the generator builds them.
/// </summary>
internal static class ReportingOracleData
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, List<OrderedDictionary<string, object?>>> Cache =
        new(StringComparer.Ordinal);

    public static List<OrderedDictionary<string, object?>> Load(string fileName) => Cache.GetOrAdd(fileName, static name =>
    {
        var path = Path.Combine(RepoPaths.Root, "gui", "DriftBuster.Backend.Tests", "Reporting", "Data", name);
        if (!PythonJson.TryLoads(File.ReadAllText(path), out var value))
        {
            throw new InvalidDataException($"{path} is not valid JSON");
        }

        return ((List<object?>)value!).Cast<OrderedDictionary<string, object?>>().ToList();
    });

    /// <summary>A fresh copy of the case, as the generator hands each call <c>json.loads(json.dumps(case))</c>.</summary>
    public static OrderedDictionary<string, object?> FreshCase(string fileName, string name)
    {
        var entry = Load(fileName).Single(item => string.Equals((string)Map(item["case"])["name"]!, name, StringComparison.Ordinal));
        PythonJson.TryLoads(Canonicaliser.Dumps(entry["case"], indent: false, ensureAscii: true, sortKeys: false), out var copy).Should().BeTrue();
        return Map(copy);
    }

    public static OrderedDictionary<string, object?> Entry(string fileName, string name)
        => Load(fileName).Single(item => string.Equals((string)Map(item["case"])["name"]!, name, StringComparison.Ordinal));

    public static TheoryData<string> Names(string fileName)
    {
        var names = new TheoryData<string>();
        foreach (var entry in Load(fileName))
        {
            names.Add((string)Map(entry["case"])["name"]!);
        }

        return names;
    }

    public static OrderedDictionary<string, object?> Map(object? value) => (OrderedDictionary<string, object?>)value!;

    public static List<object?> List(object? value) => (List<object?>)value!;

    public static List<string> Strings(object? value) => List(value).Cast<string>().ToList();

    public static List<DetectionMatch> Matches(OrderedDictionary<string, object?> spec) => List(spec["matches"]).Select(item =>
    {
        var entry = Map(item);
        var match = new DetectionMatch(
            (string)entry["plugin"]!,
            (string)entry["format"]!,
            (string?)entry.GetValueOrDefault("variant"),
            PythonBuiltins.Float(entry["confidence"]),
            entry.TryGetValue("reasons", out var reasons) ? Strings(reasons) : [],
            entry.GetValueOrDefault("metadata") as OrderedDictionary<string, object?>);
        if (entry.TryGetValue("validate", out var validate) && (bool)validate!)
        {
            match.Metadata = DetectionMetadata.ValidateDetectionMetadata(match, DetectionCatalog.Default);
        }

        return match;
    }).ToList();

    public static (RedactionFilter? Redactor, IReadOnlyList<string>? MaskTokens, string Placeholder) Redaction(OrderedDictionary<string, object?> spec)
    {
        RedactionFilter? redactor = null;
        if (spec.TryGetValue("redactor", out var raw))
        {
            var settings = Map(raw);
            redactor = new RedactionFilter(Strings(settings["tokens"]), (string)settings["placeholder"]!);
        }

        var maskTokens = spec.TryGetValue("mask_tokens", out var tokens) ? Strings(tokens) : null;
        var placeholder = spec.TryGetValue("placeholder", out var text) ? (string)text! : RedactionFilter.DefaultPlaceholder;
        return (redactor, maskTokens, placeholder);
    }

    public static List<object> HuntHits(OrderedDictionary<string, object?> spec)
    {
        var hits = new List<object>();
        foreach (var item in spec.TryGetValue("hunt_hits", out var raw) ? List(raw) : [])
        {
            var entry = Map(item);
            if (string.Equals((string)entry["kind"]!, "mapping", StringComparison.Ordinal))
            {
                hits.Add(Map(entry["value"]));
                continue;
            }

            var ruleSpec = Map(entry["rule"]);
            var rule = new HuntRule(
                (string)ruleSpec["name"]!,
                (string)ruleSpec["description"]!,
                (string?)ruleSpec.GetValueOrDefault("token_name"),
                ruleSpec.TryGetValue("keywords", out var keywords) ? Strings(keywords) : null,
                ruleSpec.TryGetValue("patterns", out var patterns) ? Strings(patterns) : null);
            hits.Add(new HuntFinding(rule, (string)entry["path"]!, (int)entry["line_number"]!, (string)entry["excerpt"]!, []));
        }

        return hits;
    }

    public static List<object> Diffs(OrderedDictionary<string, object?> spec)
    {
        var diffs = new List<object>();
        foreach (var item in spec.TryGetValue("diffs", out var raw) ? List(raw) : [])
        {
            var entry = Map(item);
            if (string.Equals((string)entry["kind"]!, "mapping", StringComparison.Ordinal))
            {
                diffs.Add(Map(entry["value"]));
                continue;
            }

            diffs.Add(DiffBuilder.BuildUnifiedDiff(
                (string)entry["before"]!,
                (string)entry["after"]!,
                (string?)entry.GetValueOrDefault("content_type") ?? "text",
                (string?)entry.GetValueOrDefault("from_label") ?? "before",
                (string?)entry.GetValueOrDefault("to_label") ?? "after",
                (string?)entry.GetValueOrDefault("label"),
                maskTokens: entry.TryGetValue("mask_tokens", out var tokens) ? Strings(tokens) : null));
        }

        return diffs;
    }
}
