using DriftBuster.Backend.Diff;
using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Reporting;

using static DriftBuster.Backend.Tests.Reporting.ReportingOracleData;

namespace DriftBuster.Backend.Tests.Reporting;

/// <summary>
/// The reporting adapters against CPython on <c>Data/*_cases.json</c> (written by <c>tools/parity/gen_report_cases.py</c>):
/// <c>html.escape</c> over every ASCII character, unpaired surrogates and non-BMP text, <c>{:.2f}</c>, <c>summarise_detections</c>,
/// the whole HTML page, JSON lines (both key orders, rendered and streamed) and snapshot files at several indents, byte for byte.
/// </summary>
[Collection(ReportingSeamCollection.Name)]
public sealed class ReportingOracleTests : IDisposable
{
    private readonly DirectoryInfo _tmp = Directory.CreateTempSubdirectory("driftbuster-reporting-oracle-");
    private readonly Func<PythonDateTime> _htmlNow = HtmlReport.UtcNow;
    private readonly Func<PythonDateTime> _snapshotNow = SnapshotManifest.UtcNow;

    public static TheoryData<string> HtmlNames => Names("html_cases.json");

    public static TheoryData<string> JsonLinesNames => Names("json_lines_cases.json");

    public static TheoryData<string> SnapshotNames => Names("snapshot_cases.json");

    public static TheoryData<string> SummaryNames => Names("summary_cases.json");

    public void Dispose()
    {
        HtmlReport.UtcNow = _htmlNow;
        SnapshotManifest.UtcNow = _snapshotNow;
        _tmp.Delete(recursive: true);
    }

    [Fact]
    public void HtmlEscapeMatchesPython()
    {
        var cases = Load("escape_cases.json");
        cases.Should().HaveCountGreaterThan(128);
        foreach (var entry in cases)
        {
            ReportValues.Escape((string)entry["input"]!).Should().Be((string)entry["escaped"]!, PythonRepr.StrRepr((string)entry["input"]!));
        }
    }

    [Fact]
    public void FixedTwoDigitFormatMatchesPython()
    {
        foreach (var entry in Load("format_cases.json"))
        {
            var value = PythonBuiltins.Float(entry["value"]);
            ReportValues.FormatFixed(value, 2).Should().Be((string)entry["fixed2"]!, PythonRepr.Float(value));
        }
    }

    [Theory]
    [MemberData(nameof(SummaryNames))]
    public void SummaryMatchesPython(string name)
    {
        var spec = FreshCase("summary_cases.json", name);
        var summary = DetectionSummary.Summarise(Matches(spec));
        Canonicaliser.Dumps(summary, indent: false, ensureAscii: true, sortKeys: false).Should().Be((string)Entry("summary_cases.json", name)["json"]!);
    }

    [Theory]
    [MemberData(nameof(HtmlNames))]
    public void HtmlReportMatchesPython(string name)
    {
        var entry = Entry("html_cases.json", name);
        var spec = FreshCase("html_cases.json", name);
        var now = PythonDateTime.FromIsoFormat((string)entry["now"]!);
        HtmlReport.UtcNow = () => now;
        var (redactor, maskTokens, placeholder) = Redaction(spec);

        var html = HtmlReport.Render(
            Matches(spec),
            title: spec.TryGetValue("title", out var title) ? (string)title! : "DriftBuster Report",
            diffs: spec.ContainsKey("diffs") ? Diffs(spec) : null,
            profileSummary: spec.GetValueOrDefault("profile_summary") as OrderedDictionary<string, object?>,
            huntHits: spec.ContainsKey("hunt_hits") ? HuntHits(spec) : null,
            redactor: redactor,
            maskTokens: maskTokens,
            placeholder: placeholder,
            extraMetadata: spec.GetValueOrDefault("extra_metadata") as OrderedDictionary<string, object?>,
            warnings: spec.TryGetValue("warnings", out var warnings) ? Strings(warnings) : null,
            legalNotice: (string?)spec.GetValueOrDefault("legal_notice"));

        html.Should().Be((string)entry["html"]!);
    }

    [Theory]
    [MemberData(nameof(JsonLinesNames))]
    public void JsonLinesMatchPython(string name)
    {
        var entry = Entry("json_lines_cases.json", name);
        foreach (var sortKeys in new[] { true, false })
        {
            var suffix = sortKeys ? "sorted" : "unsorted";
            Render(FreshCase("json_lines_cases.json", name), sortKeys, stream: null).Should().Be((string)entry["render_" + suffix]!);
            using var writer = new StringWriter();
            Render(FreshCase("json_lines_cases.json", name), sortKeys, writer);
            writer.ToString().Should().Be((string)entry["write_" + suffix]!);
        }
    }

    private static string Render(OrderedDictionary<string, object?> spec, bool sortKeys, StringWriter? stream)
    {
        var (redactor, maskTokens, placeholder) = Redaction(spec);
        var hits = HuntHits(spec);
        var profileSummary = spec.GetValueOrDefault("profile_summary") as OrderedDictionary<string, object?>;
        var extraMetadata = spec.GetValueOrDefault("extra_metadata") as OrderedDictionary<string, object?>;
        if (stream is null)
        {
            return JsonLinesReport.RenderJsonLines(Matches(spec), profileSummary, hits.Count > 0 ? hits : null, redactor, maskTokens, placeholder, extraMetadata, sortKeys);
        }

        JsonLinesReport.WriteJsonLines(Matches(spec), stream, profileSummary, hits.Count > 0 ? hits : null, redactor, maskTokens, placeholder, extraMetadata, sortKeys);
        return string.Empty;
    }

    [Theory]
    [MemberData(nameof(SnapshotNames))]
    public void SnapshotFileMatchesPython(string name)
    {
        var entry = Entry("snapshot_cases.json", name);
        var spec = FreshCase("snapshot_cases.json", name);
        var now = PythonDateTime.Create(2026, 9, 15, 18, 22, 5, 123456, PythonFixedOffset.Utc);
        SnapshotManifest.UtcNow = () => now;
        var (redactor, maskTokens, placeholder) = Redaction(spec);
        var destination = Path.Combine(_tmp.FullName, "nested", name + ".json");

        SnapshotManifest.Write(
            Matches(spec),
            destination,
            @operator: (string?)spec.GetValueOrDefault("operator"),
            outputName: (string?)spec.GetValueOrDefault("output_name"),
            redactor: redactor,
            maskTokens: maskTokens,
            placeholder: placeholder,
            legalMetadata: spec.GetValueOrDefault("legal_metadata") as OrderedDictionary<string, object?>,
            indent: (int)spec["indent"]!,
            extraMetadata: spec.GetValueOrDefault("extra_metadata") as OrderedDictionary<string, object?>);

        File.ReadAllText(destination).Should().Be(((string)entry["text"]!).Replace("\n", Environment.NewLine, StringComparison.Ordinal));
    }
}
