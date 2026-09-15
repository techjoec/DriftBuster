using System.Collections;
using System.Text;
using System.Text.RegularExpressions;

using DriftBuster.Backend.Detection;
using DriftBuster.Backend.Detection.Plugins;
using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Profiles.Detection;

namespace DriftBuster.Backend.Tests.Detection;

/// <summary>
/// Mirror of tests/core/test_detector.py plus the doctests in detector.py.
/// </summary>
public sealed class DetectorTests : IDisposable
{
    private readonly DirectoryInfo _tmp = Directory.CreateTempSubdirectory("driftbuster-detector-");

    public void Dispose() => _tmp.Delete(recursive: true);

    private string TmpPath(params string[] segments) => Path.Combine([_tmp.FullName, .. segments]);

    private string WriteText(string relative, string content)
    {
        var path = TmpPath(relative.Split('/'));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }

    private sealed class XmlRecordingPlugin : IFormatPlugin
    {
        public string Name => "test-xml-recorder";

        public int Priority => 5;

        public string Version => "0.0.0";

        public DetectionMatch? Detect(string path, byte[] sample, string? text)
        {
            if (text is null)
            {
                return null;
            }

            var metadata = new OrderedDictionary<string, object?>(StringComparer.Ordinal) { ["sample_length"] = sample.Length };
            return new DetectionMatch(Name, "xml", "generic", 0.6, ["matched fixture"], metadata);
        }
    }

    private sealed class StaticPlugin : IFormatPlugin
    {
        public string Name => "static-fixture";

        public int Priority => 0;

        public string Version => "0.0.0";

        public DetectionMatch? Detect(string path, byte[] sample, string? text)
        {
            using var handle = File.OpenRead(path);
            _ = handle.ReadByte();
            return new DetectionMatch(Name, "xml", null, 1.0, ["Static match for doctest"]);
        }
    }

    private sealed class PriorityPlugin : IFormatPlugin
    {
        public string Name => "priority-fixture";

        public int Priority => 100;

        public string Version => "0.0.0";

        public DetectionMatch? Detect(string path, byte[] sample, string? text)
            => new(Name, "xml", "generic", 0.9, ["Manual ordering"]);
    }

    private sealed class UnknownFormatPlugin : IFormatPlugin
    {
        public string Name => "static-fixture";

        public int Priority => 0;

        public string Version => "0.0.0";

        public DetectionMatch? Detect(string path, byte[] sample, string? text)
            => new(Name, "fixture", null, 1.0, ["Static match for doctest"]);
    }

    private sealed class DummyPlugin : IFormatPlugin
    {
        public string Name => "dummy";

        public int Priority => 10;

        public string Version => "0.0.0";

        public DetectionMatch? Detect(string path, byte[] sample, string? text) => null;
    }

    private sealed class BudgetPlugin : IFormatPlugin
    {
        public string Name => "budget";

        public int Priority => 1;

        public string Version => "0.0.0";

        public DetectionMatch? Detect(string path, byte[] sample, string? text)
        {
            if (sample.Length == 0)
            {
                return null;
            }

            var metadata = new OrderedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["catalog_format"] = "structured-config-xml",
                ["catalog_variant"] = "sample",
            };
            return new DetectionMatch(Name, "structured-config-xml", "sample", 0.5, ["budget test"], metadata);
        }
    }

    private sealed class SimplePlugin : IFormatPlugin
    {
        public string Name => "simple";

        public int Priority => 1;

        public string Version => "0.0.0";

        public DetectionMatch? Detect(string path, byte[] sample, string? text)
            => text is null ? null : new DetectionMatch(Name, "xml", null, 0.5, ["manual"]);
    }

    private sealed class ExplodingOpenDetector(Action<string, Exception>? onError) : Detector(onError: onError)
    {
        protected internal override Stream OpenFile(string path) => throw new IOException("boom");
    }

    private sealed class RecordingStore : IProfileMatcher
    {
        public List<string?> Paths { get; } = [];

        public IReadOnlyList<AppliedProfileConfig> MatchingConfigs(IReadOnlySet<string> tags, string? relativePath)
        {
            Paths.Add(relativePath);
            return [];
        }
    }

    private sealed class DummyDetector(IReadOnlyList<(string Path, DetectionMatch? Match)> paths) : Detector(plugins: [], sortPlugins: false)
    {
        public override IReadOnlyList<(string Path, DetectionMatch? Match)> ScanPath(string root, string glob = "**/*", bool resetBudget = true) => paths;
    }

    private sealed class ExplodingEnumerateDetector(Action<string, Exception>? onError) : Detector(onError: onError)
    {
        protected internal override IReadOnlyList<string> EnumerateFiles(string root, string glob) => throw new IOException("nope");
    }

    private sealed class TolerantDetector : Detector
    {
        public TolerantDetector()
            : base(plugins: [], sortPlugins: false)
        {
        }

        public List<(string Path, Exception Error)> Errors { get; } = [];

        public string? FlakyIsFilePath { get; set; }

        public string? FailingEnumerateRoot { get; set; }

        public bool ExplodeOnOpen { get; set; }

        protected internal override void HandleError(string path, DetectorIOException error) => Errors.Add((path, error));

        protected internal override bool IsFile(string path)
        {
            if (FlakyIsFilePath is not null && string.Equals(path, FlakyIsFilePath, StringComparison.Ordinal))
            {
                throw new IOException("blocked");
            }

            return base.IsFile(path);
        }

        protected internal override IReadOnlyList<string> EnumerateFiles(string root, string glob)
        {
            if (FailingEnumerateRoot is not null && string.Equals(root, FailingEnumerateRoot, StringComparison.Ordinal))
            {
                throw new IOException("boom");
            }

            return base.EnumerateFiles(root, glob);
        }

        protected internal override Stream OpenFile(string path)
            => ExplodeOnOpen ? throw new IOException("boom") : base.OpenFile(path);
    }

    // detector.py module doctest: a static plugin wins, and with sort_plugins=False registration order beats priority.
    // In Python the doctest itself raises MetadataValidationError because "fixture" is not a catalog format; the
    // port asserts that outcome and then the ordering claims with a catalog format.
    [Fact]
    public void ModuleDoctestStaticAndManualOrdering()
    {
        var self = WriteText("detector.py", "\"\"\"doctest fixture\"\"\"\n");

        var unknown = () => Detector.ScanFileWithDefaults(self, sampleSize: 64, plugins: [new UnknownFormatPlugin()]);
        unknown.Should().Throw<MetadataValidationError>().WithMessage("Unknown catalog format: fixture");

        var result = Detector.ScanFileWithDefaults(self, sampleSize: 64, plugins: [new StaticPlugin()]);
        result!.FormatName.Should().Be("xml");

        var manual = new Detector(plugins: [new PriorityPlugin(), new StaticPlugin()], sampleSize: 32, sortPlugins: false);
        manual.ScanFile(self)!.PluginName.Should().Be("priority-fixture");

        var sorted = new Detector(plugins: [new PriorityPlugin(), new StaticPlugin()], sampleSize: 32);
        sorted.ScanFile(self)!.PluginName.Should().Be("static-fixture");
    }

    // Detector class doctest: a plugin returning None yields no match.
    [Fact]
    public void ClassDoctestDummyPluginReturnsNull()
    {
        var self = WriteText("detector.py", "content");
        var detector = new Detector(plugins: [new DummyPlugin()], sampleSize: 64);
        detector.ScanFile(self).Should().BeNull();
    }

    [Fact]
    public void ScanFileEnrichesMetadata()
    {
        var target = WriteText("sample.xml", "<?xml version=\"1.0\"?><root><value>text</value></root>");

        var detector = new Detector(plugins: [new XmlRecordingPlugin()], sampleSize: 4, sortPlugins: false);
        var match = detector.ScanFile(target);

        match.Should().NotBeNull();
        match!.PluginName.Should().Be("test-xml-recorder");
        match.Metadata.Should().NotBeNull();
        match.Metadata!["catalog_version"].Should().Be("0.0.3");
        match.Metadata["catalog_format"].Should().Be("xml");
        match.Metadata["catalog_variant"].Should().Be("generic");
        match.Metadata["bytes_sampled"].Should().Be(4);
        match.Metadata["encoding"].Should().Be("utf-8");
        match.Metadata["sample_truncated"].Should().Be(true);
        match.Reasons.Should().Contain("Decoded Content Using Utf-8 Encoding");
        match.Reasons.Should().Contain("Truncated Sample To 4B");
    }

    [Fact]
    public void DetectorClampsRequestedSampleSize()
    {
        var target = TmpPath("large.bin");
        File.WriteAllBytes(target, Enumerable.Repeat((byte)'a', 600_000).ToArray());

        var warnings = new List<string>();
        var detector = new Detector(plugins: [new XmlRecordingPlugin()], sampleSize: 600_000, sortPlugins: false, onWarning: warnings.Add);
        var match = detector.ScanFile(target);

        match.Should().NotBeNull();
        match!.Metadata.Should().NotBeNull();
        match.Metadata!["bytes_sampled"].Should().Be(512 * 1024);
        match.Metadata["sample_truncated"].Should().Be(true);
        match.Metadata["sample_length"].Should().Be(512 * 1024);
        warnings.Should().ContainSingle().Which.Should().Be("Sample size 600000 exceeds 524288 bytes; clamping to guardrail.");
    }

    [Fact]
    public void ScanWithProfilesAttachesMatches()
    {
        var targetDir = TmpPath("configs");
        Directory.CreateDirectory(targetDir);
        WriteText("configs/appsettings.json", "{\"Logging\": {\"LogLevel\": \"Information\"}}");

        var detector = new Detector();

        var profile = new DetectionProfile(
            "prod",
            tags: ["prod"],
            configs:
            [
                new DetectionProfileConfig(
                    "cfg-prod",
                    path: "appsettings.json",
                    expectedFormat: "json",
                    expectedVariant: "structured-settings-json"),
            ]);
        var store = new DetectionProfileStore([profile]);

        var results = detector.ScanWithProfiles(targetDir, store, tags: ["prod"]);

        results.Should().ContainSingle();
        var profiled = results[0];
        profiled.Detection.Should().NotBeNull();
        profiled.Detection!.FormatName.Should().Be("json");
        profiled.Detection.Variant.Should().Be("structured-settings-json");
        profiled.Profiles.Should().NotBeEmpty();
        var applied = profiled.Profiles[0];
        applied.Profile.Name.Should().Be("prod");
        applied.Config.Identifier.Should().Be("cfg-prod");
    }

    // With the whole registry, a run of blank lines before "[s]\nk=v\n" is claimed by toml (priority 165) before ini
    // (170) on both sides: toml/generic/0.65 with a zero-space key_value_spacing profile. Only --plugins ini yields
    // sectioned-ini 0.825, which is what IniPluginTests asserts on the plugin alone.
    [Theory]
    [InlineData(5000)]
    [InlineData(30000)]
    public void BlankRunBeforeSectionIsClaimedByTomlUnderTheFullRegistry(int blankLines)
    {
        var path = WriteText($"blank-{blankLines}-then-section.ini", new string('\n', blankLines) + "[s]\nk=v\n");
        var errors = new List<Exception>();

        var match = new Detector(onError: (_, exc) => errors.Add(exc)).ScanFile(path);

        errors.Should().BeEmpty();
        match!.PluginName.Should().Be("toml");
        match.Variant.Should().Be("generic");
        match.Confidence.Should().BeApproximately(0.65, 1e-9);
        var spacing = match.Metadata!["key_value_spacing"].Should().BeOfType<OrderedDictionary<string, object?>>().Subject;
        spacing["before"].Should().Be(0);
        spacing["after"].Should().Be(0);
    }

    [Fact]
    public void DetectorRejectsInvalidSampleSize()
    {
        var act = () => new Detector(sampleSize: 0);
        act.Should().Throw<ArgumentOutOfRangeException>().WithMessage("sample_size must be a positive integer*");

        var budget = () => new Detector(maxTotalSampleBytes: 0);
        budget.Should().Throw<ArgumentOutOfRangeException>().WithMessage("max_total_sample_bytes must be a positive integer*");
    }

    private sealed class TimingOutPlugin : IFormatPlugin
    {
        public string Name => "slow";

        public int Priority => 1;

        public string Version => "0.0.0";

        public DetectionMatch? Detect(string path, byte[] sample, string? text)
            => throw new RegexMatchTimeoutException(text ?? string.Empty, "x", TimeSpan.FromSeconds(2));
    }

    // A plugin whose pattern hits its match timeout reports the file through HandleError like an unreadable one
    // (Python has no timeouts); a tolerant handler keeps the walk going and later plugins are not consulted.
    [Fact]
    public void PluginRegexTimeoutIsReportedThroughHandleError()
    {
        var target = TmpPath("slow.ini");
        File.WriteAllText(target, "[s]\nk=v\n");
        var reported = new List<(string Path, Exception Error)>();
        var strict = new Detector(plugins: [new TimingOutPlugin(), new IniPlugin()], onError: (path, exc) => reported.Add((path, exc)));

        var act = () => strict.ScanFile(target);
        act.Should().Throw<DetectorIOException>().Which.Reason.Should().Be("slow plugin timed out matching the sample");
        reported.Should().ContainSingle().Which.Error.Should().BeOfType<DetectorIOException>()
            .Which.InnerException.Should().BeOfType<RegexMatchTimeoutException>();

        var tolerant = new SwallowingDetector([new TimingOutPlugin(), new IniPlugin()]);
        File.WriteAllText(TmpPath("other.ini"), "[t]\nk=v\n");
        var results = tolerant.ScanPath(_tmp.FullName);
        results.Select(entry => (Path.GetFileName(entry.Path), entry.Match)).Should().Equal(("other.ini", null), ("slow.ini", null));
        tolerant.Errors.Select(Path.GetFileName).Should().Equal("other.ini", "slow.ini");
    }

    private sealed class SwallowingDetector(IEnumerable<IFormatPlugin> plugins) : Detector(plugins: plugins)
    {
        public List<string> Errors { get; } = [];

        protected internal override void HandleError(string path, DetectorIOException error) => Errors.Add(path);
    }

    [Fact]
    public void DetectorHandlesIoError()
    {
        var target = TmpPath("missing.txt");
        var captured = new List<string>();

        var detector = new Detector(onError: (path, _) => captured.Add(path));
        var missing = () => detector.ScanFile(target);
        missing.Should().Throw<FileNotFoundException>();
        captured.Should().BeEmpty(); // File existence check happens before callback.

        File.WriteAllText(target, "data");
        var errors = new List<Exception>();
        var exploding = new ExplodingOpenDetector((path, exc) =>
        {
            captured.Add(path);
            errors.Add(exc);
        });

        var act = () => exploding.ScanFile(target);
        act.Should().Throw<DetectorIOException>();
        captured[^1].Should().Be(target);
        errors.Should().NotBeEmpty();
        errors[^1].Message.Should().Contain("boom");
    }

    [Fact]
    public void ScanPathHandlesErrors()
    {
        var root = TmpPath("root");
        Directory.CreateDirectory(root);
        WriteText("root/a.txt", "data");
        var calls = new List<string>();

        var detector = new ExplodingEnumerateDetector((path, _) => calls.Add(path));

        var act = () => detector.ScanPath(root);
        act.Should().Throw<DetectorIOException>().WithMessage($"{root}: nope");
        calls.Should().NotBeEmpty();
        calls[^1].Should().Be(root);
    }

    [Fact]
    public void ScanPathSwallowsOsErrorWhenHandlerReturns()
    {
        var root = TmpPath("root");
        Directory.CreateDirectory(root);

        var detector = new TolerantDetector { FlakyIsFilePath = root };

        var result = detector.ScanPath(root);

        result.Should().BeEmpty();
        detector.Errors.Should().NotBeEmpty();
        detector.Errors[0].Error.Should().BeOfType<DetectorIOException>();
    }

    [Fact]
    public void ScanWithProfilesRequiresStore()
    {
        var detector = new Detector();
        var act = () => detector.ScanWithProfiles(TmpPath("file"), null!);
        act.Should().Throw<PythonValueException>().WithMessage("profile_store must be provided");
    }

    [Fact]
    public void ScanPathRejectsUnknownRoot()
    {
        var act = () => new Detector().ScanPath(TmpPath("missing", "dir"));
        act.Should().Throw<DetectorIOException>().Which.Message.Should().Contain("Path does not exist");
    }

    [Fact]
    public void ScanPathEnforcesTotalSampleBudget()
    {
        var root = TmpPath("configs");
        Directory.CreateDirectory(root);
        for (var index = 0; index < 3; index++)
        {
            WriteText($"configs/config{index}.txt", new string('x', 512));
        }

        var detector = new Detector(plugins: [new BudgetPlugin()], sampleSize: 512, maxTotalSampleBytes: 1024, sortPlugins: false, onWarning: _ => { });

        var results = detector.ScanPath(root);

        results.Should().HaveCount(2);
        detector.SampleBudgetExhausted.Should().BeTrue();
        detector.SampleBudgetRemaining.Should().Be(0);
        var finalMatch = results[^1].Match;
        finalMatch.Should().NotBeNull();
        finalMatch!.Metadata.Should().NotBeNull();
        finalMatch.Metadata!["sample_budget_exhausted"].Should().Be(true);
        finalMatch.Reasons.Should().Contain(reason => reason.ToLowerInvariant().Contains("sampling budget exhausted", StringComparison.Ordinal));
        finalMatch.Reasons.Should().Contain("Sampling Budget Exhausted After 512B");
    }

    [Fact]
    public void ScanPathConvenienceWithFile()
    {
        var target = WriteText("file.txt", "content");
        var results = Detector.ScanPathWithDefaults(target);
        results.Should().NotBeEmpty();
        results[0].Path.Should().Be(target);
    }

    [Fact]
    public void ScanFileConvenience()
    {
        var target = WriteText("data.txt", "payload");

        var match = Detector.ScanFileWithDefaults(target, plugins: [new SimplePlugin()], sortPlugins: false);

        match.Should().NotBeNull();
        match!.PluginName.Should().Be("simple");
    }

    [Fact]
    public void NormaliseReasonsDeduplicatesAndTitleises()
    {
        string[] reasons = [" sample-token:value ", "", "sample-token:value", "data-loaded"];
        var normalised = Detector.NormaliseReasons(reasons);
        normalised.Should().Equal("Sample-Token:Value", "Data-Loaded");
    }

    [Fact]
    public void HandleErrorWithoutCause()
    {
        var detector = new Detector();
        var error = new DetectorIOException(_tmp.FullName, "failed");
        var act = () => detector.HandleError(_tmp.FullName, error);
        act.Should().Throw<DetectorIOException>().Which.Should().BeSameAs(error);
        error.Message.Should().Be($"{_tmp.FullName}: failed");
    }

    [Fact]
    public void ScanPathSwallowsGlobErrorsWhenHandlerSuppresses()
    {
        var root = TmpPath("root");
        Directory.CreateDirectory(root);

        var detector = new TolerantDetector { FailingEnumerateRoot = root };

        var results = detector.ScanPath(root);

        results.Should().BeEmpty();
        detector.Errors.Should().NotBeEmpty();
        detector.Errors[0].Path.Should().Be(root);
    }

    [Fact]
    public void ScanPathContinuesWhenIndividualFileErrors()
    {
        var root = TmpPath("root");
        Directory.CreateDirectory(root);
        var badFile = WriteText("root/bad.txt", "bad");
        var goodFile = WriteText("root/good.txt", "good");

        var detector = new TolerantDetector { FlakyIsFilePath = badFile };

        var results = detector.ScanPath(root);

        results.Should().Contain((goodFile, (DetectionMatch?)null));
        detector.Errors.Should().NotBeEmpty();
        detector.Errors[0].Path.Should().Be(badFile);
    }

    [Fact]
    public void ScanWithProfilesFallsBackToFilename()
    {
        var store = new RecordingStore();

        var outside = Path.Combine(_tmp.Parent!.FullName, $"external-{Guid.NewGuid():N}.txt");
        File.WriteAllText(outside, "content");
        try
        {
            var detector = new DummyDetector([(outside, null)]);
            detector.ScanWithProfiles(_tmp.FullName, store, tags: null);

            store.Paths.Should().Equal(Path.GetFileName(outside));
        }
        finally
        {
            File.Delete(outside);
        }

        store.Paths.Clear();
        var sourceFile = WriteText("file.txt", "data");

        var fileDetector = new DummyDetector([(sourceFile, null)]);
        fileDetector.ScanWithProfiles(sourceFile, store, tags: null);

        store.Paths.Should().Equal("file.txt");
    }

    [Fact]
    public void ScanFileReturnsNoneWhenHandlerSuppresses()
    {
        var target = WriteText("data.txt", "content");

        var detector = new TolerantDetector { ExplodeOnOpen = true };

        var result = detector.ScanFile(target);
        result.Should().BeNull();
        detector.Errors.Should().NotBeEmpty();
    }

    // Port-specific: the walk order is sorted(root.glob()) order (component-wise, by code point) and reparse points
    // are not followed.
    [Fact]
    public void ScanPathWalksInPurePathOrderAndSkipsSymlinkedDirectories()
    {
        var root = TmpPath("tree");
        Directory.CreateDirectory(root);
        WriteText("tree/b.txt", "b");
        WriteText("tree/a/z.txt", "z");
        WriteText("tree/a.txt", "a");
        WriteText("tree/B.txt", "B");
        WriteText("tree/a/sub/y.txt", "y");
        var outside = TmpPath("outside");
        Directory.CreateDirectory(outside);
        WriteText("outside/linked.txt", "linked");
        File.CreateSymbolicLink(Path.Combine(root, "link"), outside);

        var detector = new Detector(plugins: [], sortPlugins: false);
        var results = detector.ScanPath(root);

        results.Select(entry => Path.GetRelativePath(root, entry.Path).Replace('\\', '/'))
            .Should().Equal("B.txt", "a/sub/y.txt", "a/z.txt", "a.txt", "b.txt");
    }

    // Port-specific: Path.is_file() is false for a dangling symlink, so the entry is skipped rather than raised.
    [Fact]
    public void ScanPathSkipsDanglingSymlinksAndScansLinkedFiles()
    {
        var root = TmpPath("links");
        Directory.CreateDirectory(root);
        var target = WriteText("links/real/target.conf", "alpha one\nbeta two\ngamma three\ndelta four\n");
        File.CreateSymbolicLink(Path.Combine(root, "broken"), Path.Combine(root, "missing-target"));
        File.CreateSymbolicLink(Path.Combine(root, "linkfile"), target);

        var detector = new Detector(plugins: [], sortPlugins: false);
        var results = detector.ScanPath(root);

        results.Select(entry => Path.GetRelativePath(root, entry.Path).Replace('\\', '/'))
            .Should().Equal("linkfile", "real/target.conf");
        detector.IsFile(Path.Combine(root, "broken")).Should().BeFalse();
        detector.IsFile(Path.Combine(root, "linkfile")).Should().BeTrue();
        var act = () => detector.ScanFile(Path.Combine(root, "broken"));
        act.Should().Throw<FileNotFoundException>();
    }

    // Port-specific: Path.is_file() is a plain stat, so the OS resolves a relative link target against the physical
    // directory holding the link. A scan root reached through a directory link (uplink -> ..) must therefore still
    // find rel.conf -> ../shared/x.conf, which FileSystemInfo.ResolveLinkTarget composes lexically and loses.
    [Fact]
    public void ScanPathResolvesRelativeLinksAgainstThePhysicalDirectory()
    {
        var shared = WriteText("shared/x.conf", "alpha one\nbeta two\ngamma three\ndelta four\n");
        WriteText("a/plain.conf", "alpha one\nbeta two\ngamma three\ndelta four\n");
        Directory.CreateDirectory(TmpPath("a", "b"));
        File.CreateSymbolicLink(TmpPath("a", "b", "uplink"), "..");
        File.CreateSymbolicLink(TmpPath("a", "rel.conf"), Path.Combine("..", "shared", "x.conf"));
        File.CreateSymbolicLink(TmpPath("a", "loop"), "loop");
        File.CreateSymbolicLink(TmpPath("a", "ping"), "pong");
        File.CreateSymbolicLink(TmpPath("a", "pong"), "ping");

        var root = TmpPath("a", "b", "uplink");
        var detector = new Detector(plugins: [], sortPlugins: false);

        detector.IsFile(Path.Combine(root, "rel.conf")).Should().BeTrue();
        detector.IsFile(Path.Combine(root, "loop")).Should().BeFalse();
        detector.IsFile(Path.Combine(root, "ping")).Should().BeFalse();
        Detector.ResolvePhysicalPath(Path.Combine(root, "rel.conf")).Should().Be(shared);
        Detector.ResolvePhysicalPath(Path.Combine(root, "loop")).Should().BeNull();

        detector.ScanPath(root).Select(entry => Path.GetRelativePath(root, entry.Path).Replace('\\', '/'))
            .Should().Equal("plain.conf", "rel.conf");
    }

    // Port-specific: a file root whose read fails reports once through on_error with a single "{path}: reason" message,
    // as DetectorIOError (not an OSError) propagates untouched through Python's scan_path.
    [Fact]
    public void ScanPathFileRootReadFailureReportsOnce()
    {
        var target = WriteText("unreadable.txt", "data");
        var calls = new List<(string Path, Exception Error)>();

        var detector = new ExplodingOpenDetector((path, exc) => calls.Add((path, exc)));
        var act = () => detector.ScanPath(target);

        act.Should().Throw<DetectorIOException>().WithMessage($"{target}: boom");
        calls.Should().ContainSingle();
        calls[0].Path.Should().Be(target);
        calls[0].Error.Message.Should().Be($"{target}: boom");
    }

    // Port-specific: _normalise_reasons uses str.strip()/str.split() (U+001C-U+001F are whitespace) and str.upper()
    // (full mapping) on the first letter, which may be an astral code point (mathematical letters have no uppercase).
    [Fact]
    public void NormaliseReasonsUsesPythonWhitespaceAndFullUppercase()
    {
        string[] reasons = ["\u001F\u00DFeta\u001F\uFB01le", "\U0001D41Astral token", "1st-\u01C6:\u03C9"];
        Detector.NormaliseReasons(reasons).Should().Equal("SSeta FIle", "\U0001D41Astral Token", "1St-\u01C4:\u03A9");
    }

    [Fact]
    public void ScanPathHonoursSuffixGlob()
    {
        var root = TmpPath("globbed");
        Directory.CreateDirectory(root);
        WriteText("globbed/one.json", "{}");
        WriteText("globbed/two.txt", "text");
        WriteText("globbed/nested/three.json", "{}");

        var detector = new Detector(plugins: [], sortPlugins: false);

        detector.ScanPath(root, "*.json").Select(entry => Path.GetFileName(entry.Path)).Should().Equal("one.json");
        detector.ScanPath(root, "**/*.json").Select(entry => Path.GetFileName(entry.Path)).Should().Equal("three.json", "one.json");
    }

    [Fact]
    public void ScanPathContinuesBudgetAcrossRootsWhenNotReset()
    {
        var first = TmpPath("first");
        var second = TmpPath("second");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        WriteText("first/a.txt", new string('x', 512));
        WriteText("second/b.txt", new string('x', 512));

        var detector = new Detector(plugins: [new BudgetPlugin()], sampleSize: 512, maxTotalSampleBytes: 512, sortPlugins: false, onWarning: _ => { });

        detector.ScanPath(first).Should().HaveCount(1);
        detector.SampleBudgetExhausted.Should().BeTrue();
        var continued = detector.ScanPath(second, resetBudget: false);
        continued.Should().ContainSingle().Which.Match.Should().BeNull();
        detector.ScanPath(second).Should().ContainSingle().Which.Match.Should().NotBeNull();
    }

    [Fact]
    public void DefaultDetectorUsesBuiltInPlugins()
    {
        var detector = new Detector();
        detector.Plugins.Select(plugin => plugin.Name).Should().Equal(DefaultPlugins.GetPlugins().Select(plugin => plugin.Name));
        detector.SampleSize.Should().Be(Detector.DefaultSampleSize);
    }
}
