using System.CommandLine;

using DriftBuster.Backend.Detection;
using DriftBuster.Backend.Detection.Catalog;
using DriftBuster.Backend.Diff;
using DriftBuster.Backend.Hunt;
using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Reporting;

namespace DriftBuster.Cli;

/// <summary>The <c>report</c> surface of <c>parity-dump</c> (py_dump.py <c>cmd_report</c>).</summary>
public static partial class ParityDump
{
    private const string ReportTimestamp = "2026-09-15T18:22:05.123456+00:00";

    /// <summary>py_dump.py <c>_report_inputs</c>: one stage's freshly built inputs.</summary>
    private sealed record ReportInputs(
        IReadOnlyDictionary<string, object?> Case,
        IEnumerable<DetectionMatch> Matches,
        List<object> Diffs,
        List<object> HuntHits,
        RedactionFilter? Redactor,
        IReadOnlyList<string>? MaskTokens,
        string Placeholder);

    private static Command BuildReport()
    {
        var caseArgument = new Argument<string>("case") { Description = "report case JSON file." };
        var command = new Command("report", "HTML report, JSON lines, detection summary and snapshot manifest of the case's inputs.");
        command.Arguments.Add(caseArgument);
        command.SetAction(parseResult =>
        {
            WriteLines(parseResult, [Report(parseResult.GetValue(caseArgument)!)]);
            return 0;
        });
        return command;
    }

    /// <summary>
    /// <see cref="HtmlReport.Render"/> (<c>html</c>), <see cref="JsonLinesReport.RenderJsonLines"/> with sorted and insertion-ordered keys
    /// (<c>json_lines</c>, <c>json_lines_unsorted</c>), <see cref="DetectionSummary.Summarise"/> (<c>summary</c>) and
    /// <see cref="SnapshotManifest.Build"/> (<c>manifest</c>) over the case, with both clocks fixed at the case's <c>timestamp</c>; each stage
    /// rebuilds its inputs from the case text, and a stage that raises is that stage's <c>error</c>.
    /// </summary>
    internal static string Report(string casePath)
    {
        var caseText = PythonUtf8.Decode(File.ReadAllBytes(casePath));
        var moment = PythonDateTime.FromIsoFormat((string?)ParseCase(caseText).GetValueOrDefault("timestamp") ?? ReportTimestamp);
        var originalHtmlNow = HtmlReport.UtcNow;
        var originalSnapshotNow = SnapshotManifest.UtcNow;
        HtmlReport.UtcNow = () => moment;
        SnapshotManifest.UtcNow = () => moment;
        try
        {
            var record = new OrderedDictionary<string, object?>(StringComparer.Ordinal);
            Stage(record, "html", () => RenderHtml(ReportInputsOf(caseText)));
            Stage(record, "json_lines", () => RenderJsonLines(ReportInputsOf(caseText), sortKeys: true));
            Stage(record, "json_lines_unsorted", () => RenderJsonLines(ReportInputsOf(caseText), sortKeys: false));
            Stage(record, "summary", () => DetectionSummary.Summarise(ReportInputsOf(caseText).Matches));
            Stage(record, "manifest", () => BuildManifest(ReportInputsOf(caseText)));
            return Keyed(record);
        }
        finally
        {
            HtmlReport.UtcNow = originalHtmlNow;
            SnapshotManifest.UtcNow = originalSnapshotNow;
        }
    }

    private static IReadOnlyDictionary<string, object?> ParseCase(string text)
        => PythonJson.TryLoads(text, out var value) && value is IReadOnlyDictionary<string, object?> mapping
            ? mapping
            : throw new InvalidDataException("The case file is not a JSON object.");

    // The adapters take these mappings typed where Python takes any value and applies dict() (or dict.update) to a truthy one: the
    // conversions run here, in the order Python reaches them (extra_metadata inside the detection payloads first, then profile_summary),
    // with Python's errors; a falsy value is none, as `x or {}` and `if x:` treat it.
    private static string RenderHtml(ReportInputs inputs)
    {
        var testCase = inputs.Case;
        var extraMetadata = Mapping(testCase.GetValueOrDefault("extra_metadata"), "extra_metadata");
        var profileSummary = Mapping(testCase.GetValueOrDefault("profile_summary"), "profile_summary");
        return HtmlReport.Render(
            inputs.Matches,
            title: testCase.TryGetValue("title", out var title) ? (string)title! : "DriftBuster Report",
            diffs: testCase.ContainsKey("diffs") ? inputs.Diffs : null,
            profileSummary: profileSummary,
            huntHits: testCase.ContainsKey("hunt_hits") ? inputs.HuntHits : null,
            redactor: inputs.Redactor,
            maskTokens: inputs.MaskTokens,
            placeholder: inputs.Placeholder,
            extraMetadata: extraMetadata,
            warnings: testCase.TryGetValue("warnings", out var warnings) ? Warnings(warnings) : null,
            legalNotice: (string?)testCase.GetValueOrDefault("legal_notice"));
    }

    private static string RenderJsonLines(ReportInputs inputs, bool sortKeys)
    {
        var extraMetadata = Mapping(inputs.Case.GetValueOrDefault("extra_metadata"), "extra_metadata");
        var profileSummary = Mapping(inputs.Case.GetValueOrDefault("profile_summary"), "profile_summary");
        return JsonLinesReport.RenderJsonLines(
            inputs.Matches,
            profileSummary,
            inputs.HuntHits.Count > 0 ? inputs.HuntHits : null,
            inputs.Redactor,
            inputs.MaskTokens,
            inputs.Placeholder,
            extraMetadata,
            sortKeys);
    }

    // dict(value) of a truthy value (PythonBuiltins.Dict, Python's errors), null for a falsy one.
    private static OrderedDictionary<string, object?>? Mapping(object? value, string what)
        => PythonBuiltins.IsTruthy(value) ? PythonBuiltins.Dict(value, what) : null;

    // list(warnings or []) then message.startswith("<span"): each str as itself; any other entry raises Python's AttributeError, when
    // the renderer reads the sequence.
    private static IEnumerable<string>? Warnings(object? value) => PythonBuiltins.IsTruthy(value) ? WarningsCore(value) : null;

    private static IEnumerable<string> WarningsCore(object? value)
    {
        foreach (var message in PythonBuiltins.Iterate(value))
        {
            yield return message as string ?? throw new PythonAttributeException($"'{PythonBuiltins.TypeName(message)}' object has no attribute 'startswith'");
        }
    }

    private static OrderedDictionary<string, object?> BuildManifest(ReportInputs inputs)
    {
        var snapshot = inputs.Case.GetValueOrDefault("snapshot") as IReadOnlyDictionary<string, object?> ?? new OrderedDictionary<string, object?>(StringComparer.Ordinal);
        // dict(extra_metadata or {}) first, legal_block.update(legal_metadata) after the records.
        var extraMetadata = Mapping(snapshot.GetValueOrDefault("extra_metadata"), "extra_metadata");
        var legalMetadata = Mapping(snapshot.GetValueOrDefault("legal_metadata"), "legal_metadata");
        return SnapshotManifest.Build(
            inputs.Matches,
            outputName: (string?)snapshot.GetValueOrDefault("output_name"),
            @operator: (string?)snapshot.GetValueOrDefault("operator"),
            redactor: inputs.Redactor,
            maskTokens: inputs.MaskTokens,
            placeholder: inputs.Placeholder,
            legalMetadata: legalMetadata,
            extraMetadata: extraMetadata);
    }

    private static List<string> Strings(object? value) => PythonBuiltins.Iterate(value).Select(item => (string)item!).ToList();

    private static List<object?> Items(object? value, string key)
        => (value as IReadOnlyDictionary<string, object?>)?.GetValueOrDefault(key) is { } items ? PythonBuiltins.Iterate(items).ToList() : [];

    private static ReportInputs ReportInputsOf(string caseText)
    {
        // Built in py_dump.py's order (diffs, hunt hits, redaction, matches), so the first input that raises is the same one.
        var testCase = ParseCase(caseText);
        var diffs = Items(testCase, "diffs").Select(item => ReportDiff((IReadOnlyDictionary<string, object?>)item!)).ToList();
        var hits = Items(testCase, "hunt_hits").Select(item => ReportHuntHit((IReadOnlyDictionary<string, object?>)item!)).ToList();
        RedactionFilter? redactor = null;
        if (testCase.GetValueOrDefault("redactor") is IReadOnlyDictionary<string, object?> settings)
        {
            redactor = new RedactionFilter(Strings(settings["tokens"]), (string)settings["placeholder"]!);
        }

        var maskTokens = testCase.TryGetValue("mask_tokens", out var tokens) ? Strings(tokens) : null;
        var placeholder = testCase.TryGetValue("placeholder", out var text) ? (string)text! : RedactionFilter.DefaultPlaceholder;
        var matches = Items(testCase, "matches").Select(item => ReportMatch((IReadOnlyDictionary<string, object?>)item!)).ToList();
        return new ReportInputs(testCase, ReportMatches(matches), diffs, hits, redactor, maskTokens, placeholder);
    }

    private const string MetadataNotAMapping = "Detection metadata must be a mapping when provided.";

    // py_dump.py _report_match: the DetectionMatch, or the MetadataValidationError summarise_metadata raises for it.
    private static (DetectionMatch Match, MetadataValidationError? Refused) ReportMatch(IReadOnlyDictionary<string, object?> spec)
    {
        var metadata = spec.GetValueOrDefault("metadata");
        var refused = metadata is null or OrderedDictionary<string, object?> ? null : new MetadataValidationError(MetadataNotAMapping);
        var match = new DetectionMatch(
            (string)spec["plugin"]!,
            (string)spec["format"]!,
            (string?)spec.GetValueOrDefault("variant"),
            PythonBuiltins.Float(spec["confidence"]),
            spec.TryGetValue("reasons", out var reasons) ? Strings(reasons) : [],
            metadata as OrderedDictionary<string, object?>);
        if (PythonBuiltins.IsTruthy(spec.GetValueOrDefault("validate")))
        {
            // validate_detection_metadata reads the metadata through _ensure_mapping first, so a refused one raises here.
            match.Metadata = refused is null ? DetectionMetadata.ValidateDetectionMetadata(match, DetectionCatalog.Default) : throw refused;
        }

        return (match, refused);
    }

    // Python's DetectionMatch holds metadata of any type and summarise_metadata refuses a non-mapping (_ensure_mapping) when the adapters
    // reach that match; the typed match cannot hold it, so the sequence raises the same error at the same position.
    private static IEnumerable<DetectionMatch> ReportMatches(IReadOnlyList<(DetectionMatch Match, MetadataValidationError? Refused)> matches)
    {
        foreach (var (match, refused) in matches)
        {
            if (refused is not null)
            {
                throw refused;
            }

            yield return match;
        }
    }

    private static object ReportDiff(IReadOnlyDictionary<string, object?> entry)
    {
        string Text(string key, string fallback) => (string?)entry.GetValueOrDefault(key) ?? fallback;
        return (string)entry["kind"]! switch
        {
            "mapping" => entry["value"]!,
            "binary" => DiffBuilder.BuildBinaryDiff(
                Convert.FromHexString((string)entry["before_hex"]!),
                Convert.FromHexString((string)entry["after_hex"]!),
                Text("from_label", "before"),
                Text("to_label", "after"),
                (string?)entry.GetValueOrDefault("label"),
                (string?)entry.GetValueOrDefault("reason")),
            _ => DiffBuilder.BuildUnifiedDiff(
                ReportText(entry["before"]),
                ReportText(entry["after"]),
                Text("content_type", "text"),
                Text("from_label", "before"),
                Text("to_label", "after"),
                (string?)entry.GetValueOrDefault("label"),
                maskTokens: entry.TryGetValue("mask_tokens", out var tokens) ? Strings(tokens) : null,
                contextLines: entry.TryGetValue("context_lines", out var context) ? (int)PythonBuiltins.Int(context) : 3),
        };
    }

    // py_dump.py _report_text: the text, or {"repeat": text, "count": n} for the text repeated n times.
    private static string ReportText(object? value) => value is IReadOnlyDictionary<string, object?> repeat
        ? string.Concat(Enumerable.Repeat((string)repeat["repeat"]!, (int)PythonBuiltins.Int(repeat["count"])))
        : (string)value!;

    private static object ReportHuntHit(IReadOnlyDictionary<string, object?> entry)
    {
        if (entry["kind"] is "mapping")
        {
            return entry["value"]!;
        }

        var rule = (IReadOnlyDictionary<string, object?>)entry["rule"]!;
        return new HuntFinding(
            new HuntRule(
                (string)rule["name"]!,
                (string)rule["description"]!,
                (string?)rule.GetValueOrDefault("token_name"),
                rule.TryGetValue("keywords", out var keywords) ? Strings(keywords) : null,
                rule.TryGetValue("patterns", out var patterns) ? Strings(patterns) : null),
            PythonPurePath.Str((string)entry["path"]!),
            (int)PythonBuiltins.Int(entry["line_number"]),
            (string)entry["excerpt"]!,
            []);
    }
}
