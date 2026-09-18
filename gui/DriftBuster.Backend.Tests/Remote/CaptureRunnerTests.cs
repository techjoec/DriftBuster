using System.Globalization;
using System.Text;

using DriftBuster.Backend.Diff;
using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Profiles.Detection;
using DriftBuster.Backend.Remote;
using DriftBuster.Backend.Tests.Infrastructure;

namespace DriftBuster.Backend.Tests.Remote;

/// <summary>
/// Capture runner behaviour: the payloads a run returns, a refused run, a sample size past the detector's range and the comparison payload.
/// </summary>
[Collection(CaptureSeamCollection.Name)]
public sealed class CaptureRunnerTests : IDisposable
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);
    private readonly DirectoryInfo _tmp = Directory.CreateTempSubdirectory("driftbuster-capture-runner-");

    public void Dispose() => _tmp.Delete(recursive: true);

    [Fact]
    public void RunCaptureReturnsThePayloadsItWrites()
    {
        var root = Tree(("app/appsettings.json", """{"Server": "db.corp.local", "Host": "h"}"""));
        using var stdout = new StringWriter(CultureInfo.InvariantCulture);
        using var stderr = new StringWriter(CultureInfo.InvariantCulture);

        var outcome = CaptureRunner.RunCapture(Options(root) with { MaskTokens = ["db.corp.local"] }, stdout, stderr);

        outcome.ExitCode.Should().Be(0);
        stderr.ToString().Should().BeEmpty();
        File.ReadAllText(outcome.SnapshotPath!).Should().Be(PlatformText(Canonicaliser.DumpsSorted(outcome.Snapshot, indent: true, ensureAscii: true)));
        File.ReadAllText(outcome.ManifestPath!).Should().Be(PlatformText(Canonicaliser.DumpsSorted(outcome.Manifest, indent: true, ensureAscii: true)));
        var detection = (OrderedDictionary<string, object?>)((List<object?>)outcome.Snapshot!["detections"]!).Should().ContainSingle().Subject!;
        detection["relative_path"].Should().Be("app/appsettings.json");
        ((OrderedDictionary<string, object?>)outcome.Manifest!["redaction"]!)["total_redactions"].Should().Be(1L);
    }

    [Fact]
    public void ARefusedRunReportsWithoutWritingAnything()
    {
        var outcome = CaptureRunner.RunCapture(Options(Path.Combine(_tmp.FullName, "missing")), TextWriter.Null, TextWriter.Null);

        outcome.Should().Be(new CaptureRunOutcome(1));
        Directory.Exists(Path.Combine(_tmp.FullName, "out")).Should().BeFalse();
    }

    [Fact]
    public void ASampleSizePastTheDetectorRangeIsClampedWithTheDetectorWarning()
    {
        var root = Tree(("a.json", "{}"));
        using var stderr = new StringWriter(CultureInfo.InvariantCulture);

        var outcome = CaptureRunner.RunCapture(Options(root) with { SampleSize = 3_000_000_000L }, TextWriter.Null, stderr);

        outcome.ExitCode.Should().Be(0);
        stderr.ToString().Should().Be("Sample size 3000000000 exceeds 524288 bytes; clamping to guardrail.\n");
    }

    [Fact]
    public void ComparisonPayloadListsKeysTokensAndTheProfileDiff()
    {
        var baseline = Json("base.json", """{"detections": [{"relative_path": "a", "detection": {"format": "json"}}], "profile_summary": {"profiles": [{"name": "p", "config_ids": ["x"]}]}, "hunt_hits": [{"rule": {"token_name": "t"}}, {}]}""");
        var current = Json("current.json", """{"detections": [{"relative_path": "b", "detection": {"format": "json", "variant": "v"}}], "profile_summary": {"profiles": [{"name": "q", "config_ids": []}]}, "hunt_hits": [{"rule": {"token_name": "t"}}, {"rule": {"token_name": "u"}}]}""");

        var comparison = CaptureRunner.CompareSnapshots(new CaptureCompareOptions(baseline, current), TextWriter.Null, TextWriter.Null);

        comparison.ExitCode.Should().Be(0);
        var payload = comparison.Payload!;
        Text(payload["added_keys"]).Should().Be("""[["b", "json", "v"]]""");
        Text(payload["removed_keys"]).Should().Be("""[["a", "json", null]]""");
        Text(payload["changed_keys"]).Should().Be("[]");
        Text(payload["profile_diff"]).Should().Be(
            """{"totals": {"baseline": {"profiles": 1, "configs": 1}, "current": {"profiles": 1, "configs": 0}}, "added_profiles": ["q"], "removed_profiles": ["p"], "changed_profiles": []}""");
        Text(payload["expected_tokens"]).Should().Be("""[{"token": "t", "baseline": 1, "current": 1, "delta": 0}, {"token": "u", "baseline": 0, "current": 1, "delta": 1}]""");
        Text(payload["unexpected_hits"]).Should().Be("""{"baseline": 1, "current": 0, "delta": -1}""");
    }

    private static string Text(object? value) => Canonicaliser.Dumps(value, indent: false, ensureAscii: true, sortKeys: false);

    private static string PlatformText(string text) => text.Replace("\n", Environment.NewLine, StringComparison.Ordinal);

    private CaptureRunOptions Options(string root) => new()
    {
        Root = root,
        OutputDir = Path.Combine(_tmp.FullName, "out"),
        CaptureId = "cap",
        Operator = "tester",
        Environment = "lab",
        Reason = "test",
        AllowUnmasked = true,
    };

    private string Tree(params (string Path, string Text)[] files)
    {
        var root = Directory.CreateDirectory(Path.Combine(_tmp.FullName, "tree")).FullName;
        foreach (var (relative, text) in files)
        {
            var path = Path.Combine(root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text, Utf8);
        }

        return root;
    }

    private string Json(string name, string text)
    {
        var path = Path.Combine(_tmp.FullName, name);
        File.WriteAllText(path, text, Utf8);
        return path;
    }

    [Fact]
    public void SerialisationGuardsAndRelativePaths()
    {
        var withoutMatch = () => CaptureRunner.SerialiseDetection(new ProfiledDetection("/root/a", null, []), "/root");
        withoutMatch.Should().Throw<ArgumentException>().WithMessage("Cannot serialise detection for paths without a match. (Parameter 'entry')");

        var capture = new OrderedDictionary<string, object?>(StringComparer.Ordinal) { ["captured_at"] = "t" };
        var manifest = () => CaptureRunner.BuildManifestPayload(capture, "s.json", "m.json", 0, 0, 0, 0, 0, 0, null, "[X]", 0, 0);
        manifest.Should().Throw<KeyNotFoundException>().WithMessage("The required key 'id' is missing.");

        CaptureRunner.RelativePath("/root/a/b.json", "/root").Should().Be("a/b.json");
        CaptureRunner.RelativePath("/root", "/root").Should().Be(".");
        CaptureRunner.RelativePath("/elsewhere/b.json", "/root").Should().Be("b.json");
        var summary = new OrderedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["tuple"] = new object?[] { 1, new HashSet<string>(StringComparer.Ordinal) { "x" } },
            ["map"] = new Dictionary<int, string> { [2] = "two" },
        };
        Canonicaliser.Dumps(CaptureRunner.NormaliseSummary(summary), indent: false, ensureAscii: true, sortKeys: false)
            .Should().Be("""{"tuple": [1, ["x"]], "map": {"2": "two"}}""");
    }
}
