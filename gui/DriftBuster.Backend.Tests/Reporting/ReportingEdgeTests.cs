using DriftBuster.Backend.Detection;
using DriftBuster.Backend.Diff;
using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Reporting;
using DriftBuster.Backend.Tests.Infrastructure;

namespace DriftBuster.Backend.Tests.Reporting;

/// <summary>
/// Python behaviours of the reporting adapters the mirrored tests and oracle cases do not reach: values the typed models cannot carry,
/// Python's error types and texts, in-place updates of the caller's nested dicts, and the port's UTF-8 write of unpaired surrogates.
/// </summary>
public sealed class ReportingEdgeTests : IDisposable
{
    private readonly DirectoryInfo _tmp = Directory.CreateTempSubdirectory("driftbuster-reporting-edge-");

    public void Dispose() => _tmp.Delete(recursive: true);

    private static OrderedDictionary<string, object?> Map(params (string Key, object? Value)[] items)
    {
        var map = new OrderedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in items)
        {
            map[key] = value;
        }

        return map;
    }

    private static DetectionMatch Match(string token = "value") => new("json", "json", "generic", 0.5, ["r"], Map(("token", token)));

    // The corrupt match of test_render_html_report_tolerates_mapping_inputs_and_corrupt_entries (confidence="invalid"), as the payload
    // mapping the renderer receives: float() raises ValueError, so the peak stays 0.00, and the match block shows str("invalid").
    [Fact]
    public void RenderDetectionSummaryToleratesInvalidConfidence()
    {
        IReadOnlyDictionary<string, object?> record = Map(("plugin", "json"), ("format", "json"), ("variant", "generic"), ("confidence", "invalid"), ("reasons", new List<object?>()), ("metadata", Map()));
        IReadOnlyDictionary<string, object?> listConfidence = Map(("format", "json"), ("variant", "generic"), ("confidence", new List<object?> { 1 }));
        IReadOnlyDictionary<string, object?> textConfidence = Map(("format", "json"), ("variant", "generic"), ("confidence", " 0.125 "));

        HtmlReport.RenderDetectionSummary([record]).Should().Contain("<td>json</td><td>generic</td><td>1</td><td>0.00</td>");
        HtmlReport.RenderDetectionSummary([record, listConfidence, textConfidence]).Should().Contain("<td>3</td><td>0.12</td>");
        HtmlReport.RenderMatch(record, 2).Should().Be(
            "<section class=\"match\"><h3>Match 2: json</h3><p><strong>Plugin:</strong> json | <strong>Variant:</strong> generic</p>"
            + "<p><strong>Confidence:</strong> invalid</p><h4>Reasons</h4><ul><li>None provided</li></ul><h4>Metadata</h4><table></table></section>");
    }

    [Fact]
    public void RunMetadataThatIsNotADictRaisesAttributeError()
    {
        var hit = Map(("path", "p"), ("run_metadata", "text"));
        var extra = Map(("run_id", "abc"));

        var html = () => HtmlReport.Render([Match()], huntHits: [hit], extraMetadata: extra);
        html.Should().Throw<PythonAttributeException>().WithMessage("'str' object has no attribute 'update'");
        var jsonl = () => JsonLinesReport.IterJsonRecords([], profileSummary: Map(("run_metadata", null)), extraMetadata: extra).ToList();
        jsonl.Should().Throw<PythonAttributeException>().WithMessage("'NoneType' object has no attribute 'update'");
    }

    [Fact]
    public void SectionRenderersReturnNothingForEmptyInputs()
    {
        HtmlReport.RenderDiffSection([]).Should().BeEmpty();
        HtmlReport.RenderHuntSection([]).Should().BeEmpty();
        HtmlReport.RenderProfileSummary(Map()).Should().BeEmpty();
        HtmlReport.FormatSafetyNotice(null).Should().BeEmpty();
        var tuple = () => JsonLinesReport.IterJsonRecords([], huntHits: [Map(("run_metadata", new object?[] { 1 }))], extraMetadata: Map(("k", 1))).ToList();
        tuple.Should().Throw<PythonAttributeException>().WithMessage("'tuple' object has no attribute 'update'");
    }

    [Fact]
    public void RunMetadataDictOfTheCallerIsUpdatedInPlace()
    {
        var nested = Map(("prior", 1));
        var summary = Map(("total", 1), ("run_metadata", nested));

        _ = JsonLinesReport.IterJsonRecords([], profileSummary: summary, redactor: new RedactionFilter(["abc"]), extraMetadata: Map(("run_id", "abc"))).ToList();

        nested.Should().Equal(new Dictionary<string, object?>(StringComparer.Ordinal) { ["prior"] = 1, ["run_id"] = "abc" });
        summary.Keys.Should().Equal("total", "run_metadata");
    }

    [Fact]
    public void DictionariesOfOtherValueTypesAreMappings()
    {
        var nested = new Dictionary<string, string>(StringComparer.Ordinal) { ["prior"] = "1" };
        var hit = new Dictionary<string, object?>(StringComparer.Ordinal) { ["path"] = "p<", ["line_number"] = 4, ["run_metadata"] = nested };
        var stringHit = new Dictionary<string, string>(StringComparer.Ordinal) { ["path"] = "q", ["excerpt"] = "e" };

        var html = HtmlReport.Render([Match()], huntHits: [hit, stringHit], extraMetadata: Map(("run_id", "abc")));

        html.Should().Contain("<li><strong>p&lt;</strong> — line 4<br/><em>None</em> <br/><code>None</code></li>");
        html.Should().Contain("<li><strong>q</strong> — line None<br/><em>None</em> <br/><code>e</code></li>");
        nested.Should().Equal(new Dictionary<string, string>(StringComparer.Ordinal) { ["prior"] = "1", ["run_id"] = "abc" });
        var records = JsonLinesReport.RenderJsonLines([], profileSummary: Map(("ids", new Dictionary<int, string> { [1] = "a" })));
        records.Should().Be("{\"payload\": {\"ids\": {\"1\": \"a\"}}, \"type\": \"profile_summary\"}");
    }

    [Fact]
    public void RedactorAndMaskTokensTogetherRaise()
    {
        var redactor = new RedactionFilter(["x"]);
        var html = () => HtmlReport.Render([Match()], redactor: redactor, maskTokens: ["y"]);
        html.Should().Throw<PythonValueException>().WithMessage("Provide either an explicit redactor or mask_tokens, not both.");
        var jsonl = () => JsonLinesReport.RenderJsonLines([Match()], redactor: redactor, maskTokens: ["y"]);
        jsonl.Should().Throw<PythonValueException>();
        var snapshot = () => SnapshotManifest.Build([Match()], redactor: redactor, maskTokens: ["y"]);
        snapshot.Should().Throw<PythonValueException>();
    }

    // _serialise_diff applies dict() to anything that is not a DiffResult: a sequence of pairs renders, the rest raises dict()'s errors.
    [Fact]
    public void ADiffGivenAsPairsIsReadAsDictReadsIt()
    {
        var pairs = new List<object?> { new List<object?> { "label", "L" }, new object?[] { "diff", "-a\n+b" }, new List<object?> { "stats", Map(("added_lines", 1)) } };
        HtmlReport.SerialiseDiff(pairs).Keys.Should().Equal("label", "diff", "stats");
        HtmlReport.Render([Match()], diffs: [pairs]).Should().Contain("<h3>L</h3><ul class=\"diff-stats\"><li>added_lines: 1</li></ul><pre>-a\n+b</pre>");

        var ints = () => HtmlReport.SerialiseDiff(new List<object?> { 1, 2 });
        ints.Should().Throw<PythonTypeException>().WithMessage("cannot convert dictionary update sequence element #0 to a sequence");
        var text = () => HtmlReport.SerialiseDiff("ab");
        text.Should().Throw<PythonValueException>().WithMessage("dictionary update sequence element #0 has length 1; 2 is required");
    }

    [Fact]
    public void WritingAReportOverADirectoryRaisesPythonsOSError()
    {
        var directory = Path.Combine(_tmp.FullName, "report.html");
        Directory.CreateDirectory(directory);

        var html = () => HtmlReport.Write([Match()], directory);
        var manifest = () => SnapshotManifest.Write([Match()], directory);

        foreach (var write in new[] { html, manifest })
        {
            var raised = write.Should().Throw<IOException>().Which;
            raised.Message.Should().Be(OSErrorTexts.DirectoryOpen(directory));
            PythonOSError.TypeName(raised.HResult).Should().Be(OSErrorTexts.DirectoryOpenType);
        }
    }

    [Fact]
    public void InputsOutsideTheMappingDomainRaiseAsPythonDoes()
    {
        var diff = () => HtmlReport.Render([Match()], diffs: [5]);
        diff.Should().Throw<PythonTypeException>().WithMessage("'int' object is not iterable");
        var hit = () => JsonLinesReport.IterJsonRecords([], huntHits: ["text"]).ToList();
        hit.Should().Throw<PythonAttributeException>().WithMessage("'str' object has no attribute 'rule'");
        var set = () => JsonLinesReport.RenderJsonLines([Match()], extraMetadata: Map(("tags", new HashSet<string>(StringComparer.Ordinal) { "a" })));
        set.Should().Throw<PythonTypeException>().WithMessage("Object of type set is not JSON serializable");
    }

    [Fact]
    public void TupleValuesRenderWithTupleSpelling()
    {
        var html = HtmlReport.Render([Match()], extraMetadata: Map(("one", new object?[] { "a" }), ("two", new object?[] { 1, null })));
        html.Should().Contain("<tr><th>one</th><td>(&#x27;a&#x27;,)</td></tr>");
        html.Should().Contain("<tr><th>two</th><td>(1, None)</td></tr>");
        var typed = HtmlReport.Render(
            [Match()],
            extraMetadata: Map(
                ("nested", new object?[] { new object?[] { "a" }, new List<string> { "b" } }),
                ("strings", new List<string> { "x", "y" }),
                ("mapping", new Dictionary<string, object?>(StringComparer.Ordinal) { ["k"] = new List<int> { 1 } })));
        typed.Should().Contain("<tr><th>nested</th><td>((&#x27;a&#x27;,), [&#x27;b&#x27;])</td></tr>");
        typed.Should().Contain("<tr><th>strings</th><td>[&#x27;x&#x27;, &#x27;y&#x27;]</td></tr>");
        typed.Should().Contain("<tr><th>mapping</th><td>{&#x27;k&#x27;: [1]}</td></tr>");
        JsonLinesReport.RenderJsonLines([Match()], extraMetadata: Map(("two", new object?[] { 1, null }))).Should().Contain("\"two\": [1, null]");
    }

    [Fact]
    public void SnapshotIndentsBelowZeroBehaveAsZero()
    {
        var manifest = Map(("a", new List<object?> { 1, Map(("b", 2)) }), ("c", Map()));
        ReportValues.DumpsIndented(manifest, -3, ensureAscii: false).Should().Be("{\n\"a\": [\n1,\n{\n\"b\": 2\n}\n],\n\"c\": {}\n}");
        ReportValues.DumpsIndented(manifest, 3, ensureAscii: false).Should().Be("{\n   \"a\": [\n      1,\n      {\n         \"b\": 2\n      }\n   ],\n   \"c\": {}\n}");
    }

    [Fact]
    public void FormatFixedHonoursOtherPrecisions()
    {
        ReportValues.FormatFixed(2.5, 0).Should().Be("2");
        ReportValues.FormatFixed(3.5, 0).Should().Be("4");
        ReportValues.FormatFixed(0.0625, 3).Should().Be("0.062");
    }

    // Python's write_text / json.dump raise UnicodeEncodeError on an unpaired surrogate; the port writes U+FFFD (the recorded
    // operator decision for strings the runtime encodes as UTF-8).
    [Fact]
    public void UnpairedSurrogatesAreWrittenAsReplacementCharacters()
    {
        var htmlPath = Path.Combine(_tmp.FullName, "report.html");
        HtmlReport.Write([Match("\ud800")], htmlPath);
        File.ReadAllText(htmlPath, System.Text.Encoding.UTF8).Should().Contain("<td>\ufffd</td>");

        var snapshotPath = Path.Combine(_tmp.FullName, "deeper", "snapshot.json");
        SnapshotManifest.Write([Match("\ud800")], snapshotPath);
        File.ReadAllText(snapshotPath, System.Text.Encoding.UTF8).Should().Contain("\"token\": \"\ufffd\"");
    }
}
