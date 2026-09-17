using System.Text;

using DriftBuster.Backend.Diff;
using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Models;
using DriftBuster.Backend.Profiles.Run;
using DriftBuster.Backend.Tests.Infrastructure;
using DriftBuster.Backend.Tests.Secrets;

using static DriftBuster.Backend.Tests.Profiles.Run.RunProfilesTests;

namespace DriftBuster.Backend.Tests.Profiles.Run;

/// <summary>
/// CPython 3.13 output of <c>run_profiles</c> for inputs the mirrored tests leave open (the expected texts were produced by
/// <c>save_profile</c>, <c>execute_profile</c> and <c>json.dumps</c> with the temporary root spelled <c>{root}</c>), and the structured
/// source semantics, which have no Python oracle: alias, optional and exclude as the offline runner applies them.
/// </summary>
[Collection(SecretRuleCacheCollection.Name)]
public sealed class RunProfileStoreEdgeTests : IDisposable
{
    private const string ProfileJson =
        "{\n  \"baseline\": \"{root}/configs\",\n  \"description\": \"d\\u00e9\\ud83d\\ude00\\t\\\"q\\\"\",\n  \"name\": \"\\u00dcn\\u00efcode prof/1\",\n"
        + "  \"options\": {\n    \"b\": \"True\",\n    \"f\": \"1.5\",\n    \"l\": \"[1, 'x']\",\n    \"n\": \"5\",\n    \"none\": \"\"\n  },\n"
        + "  \"secret_scanner\": {\n    \"extra\": {\n      \"k\": [\n        1,\n        2.0,\n        null\n      ]\n    },\n"
        + "    \"ignore_patterns\": [\n      \"P\"\n    ],\n    \"ignore_rules\": [\n      \"b\",\n      \"a\"\n    ],\n"
        + "    \"ruleset\": {\n      \"rules\": [],\n      \"version\": \"v\"\n    }\n  },\n"
        + "  \"sources\": [\n    \"{root}/configs\",\n    \"{root}/configs/*.json\"\n  ]\n}";

    private const string FilesJson =
        "  \"files\": [\n    {\n      \"destination\": \"{root}/Profiles/demo-run/raw/20240101T000000Z/source_00/nested/secret.txt\",\n"
        + "      \"sha256\": \"9ff6ff0110bdb7e6e311dc216f685f67a8c1d04794e4e355691ae7b06e7fad4e\",\n      \"size\": 18,\n      \"source\": \"{root}/configs\"\n    },\n"
        + "    {\n      \"destination\": \"{root}/Profiles/demo-run/raw/20240101T000000Z/source_01/app.json\",\n"
        + "      \"sha256\": \"0a31f66c1655560d64e5d360556586e3762b127f6b2ae6e00dd83f2275b976a0\",\n      \"size\": 10,\n      \"source\": \"{root}/other/*.json\"\n    }\n  ],\n";

    private const string ProfileAndSecretsJson =
        "  \"profile\": {\n    \"baseline\": \"{root}/configs\",\n    \"description\": null,\n    \"name\": \"demo run\",\n    \"options\": {},\n"
        + "    \"secret_scanner\": {\n      \"ignore_patterns\": [\n        \"NEVER\"\n      ]\n    },\n"
        + "    \"sources\": [\n      \"{root}/other/*.json\",\n      \"{root}/configs\"\n    ]\n  },\n"
        + "  \"secrets\": {\n    \"findings\": [\n      {\n        \"line\": 2,\n        \"path\": \"nested/secret.txt\",\n"
        + "        \"rule\": \"PasswordAssignment\",\n        \"snippet\": \"[SECRET]\"\n      }\n    ],\n"
        + "    \"ignored_patterns\": [\n      \"NEVER\"\n    ],\n    \"ignored_rules\": [],\n"
        + "    \"messages\": [\n      \"secret candidate redacted (PasswordAssignment) from nested/secret.txt:2 -> [SECRET]\",\n"
        + "      \"scrubbed 1 potential secret line(s) from nested/secret.txt\"\n    ],\n"
        + "    \"rules_loaded\": true,\n    \"ruleset_version\": \"2024-06-01\"\n  },\n  \"timestamp\": \"20240101T000000Z\"\n}";

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly SecretRuleCacheIsolation _isolation = new();
    private readonly DirectoryInfo _tmp = Directory.CreateTempSubdirectory("driftbuster-run-profile-edge-");

    public void Dispose()
    {
        _isolation.Dispose();
        _tmp.Delete(recursive: true);
    }

    private string Root => PathText.ToPosix(_tmp.FullName);

    private string Expected(string text) => text.Replace("{root}", Root, StringComparison.Ordinal).Replace("\n", Environment.NewLine, StringComparison.Ordinal);

    private string Write(string relative, string content)
    {
        var path = Path.Combine(_tmp.FullName, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, Utf8);
        return path;
    }

    [Fact]
    public void SaveProfileWritesPythonsJsonBytes()
    {
        var configs = Path.GetDirectoryName(Write("configs/a.json", "{}"))!;
        var profile = new RunProfile(
            "\u00dcn\u00efcode prof/1",
            description: "d\u00e9\U0001F600\t\"q\"",
            sources: Sources(configs, Path.Combine(configs, "*.json")),
            baseline: configs,
            options: Map(("n", 5), ("f", 1.5), ("b", true), ("none", null), ("l", new List<object?> { 1, "x" })),
            secretScanner: Map(
                ("ignore_rules", "b a"),
                ("ignore_patterns", new List<object?> { "P" }),
                ("ruleset", Map(("version", "v"), ("rules", new List<object?>()))),
                ("extra", Map(("k", new List<object?> { 1, 2.0, null })))));

        var directory = RunProfileStore.SaveProfile(profile, baseDir: _tmp.FullName);

        PathText.Name(directory).Should().Be("\u00dcn\u00efcode-prof-1");
        File.ReadAllBytes(Path.Combine(directory, "profile.json")).Should().Equal(Utf8.GetBytes(Expected(ProfileJson)));
        var loaded = RunProfileStore.LoadProfile("\u00dcn\u00efcode prof/1", baseDir: _tmp.FullName);
        Canonicaliser.DumpsSorted(loaded.ToDict(), ensureAscii: true).Should().Be(ProfileJson.Replace("{root}", Root, StringComparison.Ordinal));
    }

    [Fact]
    public void ExecuteProfileWritesPythonsMetadataAndResult()
    {
        var configs = Path.GetDirectoryName(Path.GetDirectoryName(Write("configs/nested/secret.txt", "user = x\npassword = Hunter12345\n")))!;
        var appJson = Write("other/app.json", "{\"key\": 1}");
        var glob = Path.Combine(Path.GetDirectoryName(appJson)!, "*.json");
        var profile = new RunProfile("demo run", sources: Sources(glob, configs), baseline: configs, secretScanner: Map(("ignore_patterns", new List<object?> { "NEVER" })));

        var result = RunProfileExecutor.ExecuteProfile(profile, baseDir: _tmp.FullName, timestamp: "20240101T000000Z", cancellationToken: TestContext.Current.CancellationToken);

        var metadataText = "{\n  \"baseline\": \"{root}/configs\",\n" + FilesJson + ProfileAndSecretsJson;
        File.ReadAllText(Path.Combine(result.OutputDir, "metadata.json"), Utf8).Should().Be(Expected(metadataText));
        var resultText = "{\n" + FilesJson + "  \"output_dir\": \"{root}/Profiles/demo-run/raw/20240101T000000Z\",\n" + ProfileAndSecretsJson;
        Canonicaliser.DumpsSorted(result.ToDict(), ensureAscii: true).Should().Be(resultText.Replace("{root}", Root, StringComparison.Ordinal));
        if (!OperatingSystem.IsWindows())
        {
            File.ReadAllBytes(result.Files[0].Destination).Should().Equal(Utf8.GetBytes("user = x\n[SECRET]\n"));
        }
    }

    [Fact]
    public void DumpsEscapesEveryUnitOutsidePrintableAscii()
    {
        Canonicaliser.DumpsSorted(Map(("s", "\u007f\ud800\u2028~ ")), ensureAscii: true).Should().Be("{\n  \"s\": \"\\u007f\\ud800\\u2028~ \"\n}");
    }

    [Fact]
    public void StructuredSourcesHonourAliasOptionalAndExclude()
    {
        var logs = Path.GetDirectoryName(Write("logs/app.log", "log"))!;
        Write("logs/debug.tmp", "tmp");
        Write("logs/nested/skip.log", "skip");
        Write("logs/nested/keep.log", "keep");
        var single = Write("single.tmp", "single");
        var profile = new RunProfile(
            "structured",
            sources:
            [
                new RunProfileSource(logs) { Alias = "My Logs", Exclude = ["*.tmp", "nested/skip.*"] },
                new RunProfileSource(single) { Exclude = ["*.tmp"] },
                new RunProfileSource(Path.Combine(_tmp.FullName, "absent.txt")) { Optional = true, Alias = "absent" },
                new RunProfileSource(Path.Combine(_tmp.FullName, "gone", "*.txt")) { Optional = true },
            ]);

        var result = RunProfileExecutor.ExecuteProfile(profile, baseDir: _tmp.FullName, timestamp: "run", cancellationToken: TestContext.Current.CancellationToken);

        result.Files.Select(file => PathText.ToPosix(Path.GetRelativePath(result.OutputDir, file.Destination))).Order(StringComparer.Ordinal)
            .Should().Equal("My-Logs/app.log", "My-Logs/nested/keep.log");
        Directory.Exists(Path.Combine(result.OutputDir, "source_01")).Should().BeTrue();
        Directory.Exists(Path.Combine(result.OutputDir, "absent")).Should().BeTrue();
        Directory.Exists(Path.Combine(result.OutputDir, "source_03")).Should().BeTrue();

        var sources = (List<object?>)RunProfileStore.LoadProfile("structured", baseDir: _tmp.FullName).ToDict()["sources"]!;
        Canonicaliser.DumpsSorted(sources[0], ensureAscii: true).Should().Be(
            "{\n  \"alias\": \"My Logs\",\n  \"exclude\": [\n    \"*.tmp\",\n    \"nested/skip.*\"\n  ],\n  \"path\": \"" + PathText.ToPosix(logs) + "\"\n}");
        Canonicaliser.DumpsSorted(sources[2], ensureAscii: true).Should().Be(
            "{\n  \"alias\": \"absent\",\n  \"optional\": true,\n  \"path\": \"" + PathText.ToPosix(Path.Combine(_tmp.FullName, "absent.txt")) + "\"\n}");
    }

    [Fact]
    public void ARequiredGlobThatMatchesNothingCopiesNothing()
    {
        Directory.CreateDirectory(Path.Combine(_tmp.FullName, "empty"));
        var pattern = Path.Combine(_tmp.FullName, "empty", "*.txt");
        var profile = new RunProfile("empty-glob", sources: Sources(pattern));

        var result = RunProfileExecutor.ExecuteProfile(profile, baseDir: _tmp.FullName, cancellationToken: TestContext.Current.CancellationToken);

        result.Files.Should().BeEmpty();
        ((string)ReadMetadata(result)["baseline"]!).Should().Be(pattern);
    }

    [Fact]
    public void AnOptionalBaselineMayBeMissingInAStructuredProfile()
    {
        var missing = Path.Combine(_tmp.FullName, "missing.txt");
        var profile = new RunProfile("optional-baseline", sources: [new RunProfileSource(missing) { Optional = true }], baseline: missing);

        var act = () => RunProfileStore.ValidateProfile(profile);
        act.Should().NotThrow();

        var stringProfile = new RunProfile("string-baseline", sources: Sources(missing), baseline: missing);
        var required = () => RunProfileStore.ValidateProfile(stringProfile);
        required.Should().Throw<FileNotFoundException>().WithMessage("Path does not exist: *");
    }

    [Fact]
    public void ValidationMessagesArePythons()
    {
        var none = () => RunProfileStore.ValidateProfile(new RunProfile("none"));
        none.Should().Throw<PythonValueException>().WithMessage("At least one source must be provided.");

        var blank = () => RunProfileStore.ValidateProfile(new RunProfile("blank", sources: Sources(" \t")));
        blank.Should().Throw<PythonValueException>().WithMessage("Source paths must not be empty.");

        var existing = Write("present.txt", "x");
        var baseline = () => RunProfileStore.ValidateProfile(new RunProfile("baseline", sources: Sources(existing), baseline: existing + "x"));
        baseline.Should().Throw<PythonValueException>().WithMessage("Baseline must be one of the sources.");
    }

    [Fact]
    public void FromDictCoercesAsPythonDoes()
    {
        var profile = RunProfile.FromDict(Map(("name", 5), ("sources", "ab"), ("description", 3)));
        profile.Name.Should().Be("5");
        profile.Description.Should().Be("3");
        profile.Sources.Select(source => source.Path).Should().Equal("a", "b");
        profile.Baseline.Should().BeNull();
        profile.Options.Should().BeEmpty();
        profile.SecretScanner.Should().BeEmpty();

        var missingName = () => RunProfile.FromDict(Map(("sources", new List<object?>())));
        missingName.Should().Throw<KeyNotFoundException>();

        var badOptions = () => RunProfile.FromDict(Map(("name", "x"), ("options", new List<object?> { 1 })));
        badOptions.Should().Throw<PythonAttributeException>().WithMessage("'list' object has no attribute 'items'");
    }

    [Fact]
    public void SafeNameReplacesEveryCodePointThatIsNotAlphanumeric()
    {
        RunProfileStore.SafeName("a b/\u00e9_\U0001F600-").Should().Be("a-b-\u00e9_--");
        RunProfileStore.SafeName("x\ud800y").Should().Be("x-y");
    }

    [Fact]
    public void GlobBaseDirectoryStopsAtTheFirstMagicPart()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        RunProfileStore.GlobBaseDirectory("/a/b*/c").Should().Be("/a");
        RunProfileStore.GlobBaseDirectory("/x/y/z.txt").Should().Be("/x/y/z.txt");
        RunProfileStore.GlobBaseDirectory("/*").Should().Be("/");
        RunProfileStore.GlobBaseDirectory("*.json").Should().Be(Directory.GetCurrentDirectory());
    }

    [Fact]
    public void ListProfilesOrdersByPathCodePoints()
    {
        foreach (var name in new[] { "a", "_x", "B", "a-b" })
        {
            RunProfileStore.SaveProfile(new RunProfile(name, sources: Sources(_tmp.FullName)), baseDir: _tmp.FullName);
        }

        RunProfileStore.ListProfiles(baseDir: _tmp.FullName, TestContext.Current.CancellationToken).Select(profile => profile.Name).Should().Equal("B", "_x", "a", "a-b");
    }

    [Fact]
    public void ListProfilesRaisesOnAProfileThatIsNotJson()
    {
        Write("Profiles/broken/profile.json", "{ invalid json");

        var act = () => RunProfileStore.ListProfiles(baseDir: _tmp.FullName);
        act.Should().Throw<PythonValueException>();
    }

    [Fact]
    public void ListProfilesRaisesOpensErrorForADirectoryNamedProfileJson()
    {
        Write("Profiles/a/profile.json", """{"name": "a", "sources": ["x"]}""");
        var directory = Directory.CreateDirectory(Path.Combine(_tmp.FullName, "Profiles", "dirjson", "profile.json")).FullName;

        var act = () => RunProfileStore.ListProfiles(baseDir: _tmp.FullName, TestContext.Current.CancellationToken);

        act.Should().Throw<IOException>().Which.Message.Should().Be(OSErrorTexts.DirectoryOpen(directory));
    }

    /// <summary><c>Path(args.profile).read_text()</c> on a missing file: <c>open()</c>'s error naming the path as <c>str(Path)</c> spells it.</summary>
    [Fact]
    public void RunWithAMissingProfileFileRaisesOpensErrorForIt()
    {
        var spelled = _tmp.FullName + Path.DirectorySeparatorChar + "." + Path.DirectorySeparatorChar + "nope.json";

        var act = () => RunProfileCommands.Run(spelled, name: null, _tmp.FullName, save: false, timestamp: null, ignoreRules: null, ignorePatterns: null);

        act.Should().Throw<IOException>().Which.Message.Should()
            .Be($"[Errno 2] No such file or directory: {PythonRepr.StrRepr(Path.Combine(_tmp.FullName, "nope.json"))}");
    }

    [Fact]
    public void RunWithAnUnreadableProfileFileRaisesPermissionError()
    {
        if (!OperatingSystem.IsLinux() || Environment.IsPrivilegedProcess)
        {
            Assert.Skip("file modes refuse the read only for an unprivileged Unix user");
            return;
        }

        var path = Path.Combine(_tmp.FullName, "locked.json");
        File.WriteAllText(path, "{}");
        File.SetUnixFileMode(path, UnixFileMode.None);

        var act = () => RunProfileCommands.Run(path, name: null, _tmp.FullName, save: false, timestamp: null, ignoreRules: null, ignorePatterns: null);

        var thrown = act.Should().Throw<IOException>().Which;
        thrown.Message.Should().Be($"[Errno 13] Permission denied: {PythonRepr.StrRepr(path)}");
        thrown.HResult.Should().Be(PythonOSError.PermissionDenied);
    }

    [Theory]
    [InlineData("""{"sources": ["a", {"path": "b"}]}""", false)]
    [InlineData("""{"sources": [{"path": "b", "alias": " ", "optional": 0, "exclude": []}]}""", false)]
    [InlineData("""{"sources": [{"path": "b", "alias": "x"}]}""", true)]
    [InlineData("""{"sources": [{"path": "b", "optional": [0]}]}""", true)]
    [InlineData("""{"sources": [{"path": "b", "exclude": ""}]}""", true)]
    [InlineData("""{"sources": [{"path": " "}]}""", true)]
    [InlineData("""{"sources": [{"path": "b", "exclude": 5}]}""", true)]
    [InlineData("""{"sources": [{"registry_scan": {}}]}""", true)]
    [InlineData("""{"sources": {"x": {"alias": "y"}}}""", false)]
    [InlineData("""[{"path": "b", "alias": "x"}]""", false)]
    public void StructuredPayloadsAreTheOnesWithAStructuredSource(string payload, bool structured)
    {
        PythonJson.TryLoads(payload, out var decoded).Should().BeTrue();
        RunProfile.IsStructuredPayload(decoded).Should().Be(structured);
    }

    [Theory]
    [InlineData("""{"sources": [{"path": ""}, {"path": "f", "alias": "f"}]}""", "PythonValueException", "Profile requires a non-empty 'name'.")]
    [InlineData("""{"name": " ", "sources": [{"path": "f", "alias": "f"}]}""", "PythonValueException", "Profile requires a non-empty 'name'.")]
    [InlineData("""{"name": "n", "sources": [{"path": ""}, {"path": "f", "alias": "f"}], "options": 1}""", "PythonValueException", "Source entry requires a non-empty 'path'.")]
    [InlineData("""{"name": "n", "sources": [{"path": "f", "alias": "f"}], "baseline": ""}""", "PythonValueException", "Profile baseline must reference one of the declared sources.")]
    [InlineData("""{"name": "n", "sources": [{"path": "f", "alias": "f"}], "tags": 5, "options": 1}""", "PythonTypeException", "'int' object is not iterable")]
    [InlineData("""{"name": "n", "sources": [{"path": "f", "alias": "f"}], "options": null}""", "PythonValueException", "Profile 'options' must be a mapping if provided.")]
    [InlineData("""{"name": "n", "sources": [{"path": "f", "alias": "f"}], "options": [], "secret_scanner": 1}""", "PythonValueException", "Profile 'options' must be a mapping if provided.")]
    [InlineData("""{"name": "n", "sources": [{"path": "f", "alias": "f"}], "secret_scanner": [1]}""", "PythonValueException", "Profile 'secret_scanner' must be a mapping if provided.")]
    [InlineData("""{"name": "n", "sources": [{"path": "f", "alias": "f"}, {"sql_snapshot": {}, "path": "db"}]}""", "PythonValueException", "Run profiles do not support 'sql_snapshot' sources.")]
    [InlineData("""{"name": "n", "sources": [{"registry_scan": {"token": "t"}}]}""", "PythonValueException", "Run profiles do not support 'registry_scan' sources.")]
    [InlineData("""{"name": 7, "description": 5, "sources": [5, {"path": "f", "alias": 3, "optional": [0], "exclude": [1, "*.tmp"]}], "baseline": 5, "tags": "t", "secret_scanner": 0}""", "ok", "{'name': '7', 'description': '5', 'sources': ['5', {'path': 'f', 'alias': '3', 'optional': True, 'exclude': ['1', '*.tmp']}], 'baseline': '5', 'options': {}, 'secret_scanner': {}}")]
    public void StructuredProfilesAreReadAsTheOfflineRunnerReadsThem(string payload, string kind, string expected)
    {
        PythonJson.TryLoads(payload, out var decoded).Should().BeTrue();
        var act = () => PythonRepr.Repr(RunProfile.FromDict(decoded).ToDict());
        if (string.Equals(kind, "ok", StringComparison.Ordinal))
        {
            act.Should().NotThrow().Which.Should().Be(expected);
            return;
        }

        var thrown = act.Should().Throw<Exception>().Which;
        thrown.GetType().Name.Should().Be(kind);
        thrown.Message.Should().Be(expected);
    }

    [Fact]
    public void ABlankAliasIsDroppedByTheModelAsTheJsonReaderDropsIt()
    {
        var data = Path.GetDirectoryName(Write("data/a.txt", "a"))!;
        var definition = new RunProfileDefinition
        {
            Name = "blank-alias",
            Sources = [new RunProfileSource(data), new RunProfileSource(Path.Combine(_tmp.FullName, "missing")) { Optional = true, Alias = "  " }],
        };

        var profile = RunProfile.FromDefinition(definition);
        profile.Sources[1].Alias.Should().BeNull();
        new RunProfile("ctor", sources: [new RunProfileSource(data) { Alias = " \t" }]).Sources[0].IsPathOnly.Should().BeTrue();

        var result = RunProfileExecutor.ExecuteProfile(profile, baseDir: _tmp.FullName, timestamp: "run", cancellationToken: TestContext.Current.CancellationToken);

        Directory.EnumerateDirectories(result.OutputDir).Select(Path.GetFileName).Order(StringComparer.Ordinal).Should().Equal("source_00", "source_01");
        var reloaded = RunProfileStore.ListProfiles(baseDir: _tmp.FullName, TestContext.Current.CancellationToken).Should().ContainSingle().Subject;
        reloaded.Sources[1].Alias.Should().BeNull();
        reloaded.Sources[1].Optional.Should().BeTrue();
        RunProfile.FromDefinition(new RunProfileDefinition { Name = "b", Sources = [new RunProfileSource(data)], Baseline = string.Empty }).Baseline.Should().BeNull();
    }

    [Fact]
    public void TheModelJsonReaderReadsAnObjectSourceAsSourceFromDict()
    {
        var read = System.Text.Json.JsonSerializer.Deserialize<RunProfileSource[]>(
            """["plain", {"path": 5, "alias": 0, "optional": [1], "exclude": [1, "x", null]}, {"path": "p", "alias": true, "exclude": "one"}]""")!;

        read[0].IsPathOnly.Should().BeTrue();
        read[1].Path.Should().Be("5");
        read[1].Alias.Should().BeNull();
        read[1].Optional.Should().BeTrue();
        read[1].Exclude.Should().Equal("1", "x", "None");
        read[2].Alias.Should().Be("True");
        read[2].Exclude.Should().Equal("one");

        var empty = () => System.Text.Json.JsonSerializer.Deserialize<RunProfileSource>("""{"path": " ", "alias": "a"}""");
        empty.Should().Throw<System.Text.Json.JsonException>().WithMessage("Source entry requires a non-empty 'path'.");
    }

    [Fact]
    public void DefinitionRoundTripKeepsStructuredSources()
    {
        var definition = new RunProfileDefinition
        {
            Name = "gui",
            Sources = [new RunProfileSource("/a"), new RunProfileSource("/b") { Alias = "b", Optional = true, Exclude = ["*.tmp"] }],
            Baseline = "/a",
            Options = new Dictionary<string, string>(StringComparer.Ordinal) { ["k"] = "v" },
            SecretScanner = new SecretScannerOptions { IgnoreRules = [" r "], IgnorePatterns = [] },
        };

        var profile = RunProfile.FromDefinition(definition);
        Canonicaliser.DumpsSorted(profile.ToDict()["secret_scanner"]).Should().Be("{\n  \"ignore_rules\": [\n    \"r\"\n  ]\n}");

        var back = profile.ToDefinition();
        back.Sources.Select(source => source.Path).Should().Equal("/a", "/b");
        back.Sources[1].Alias.Should().Be("b");
        back.Sources[1].Optional.Should().BeTrue();
        back.Sources[1].Exclude.Should().Equal("*.tmp");
        back.Options.Should().Equal(new Dictionary<string, string>(StringComparer.Ordinal) { ["k"] = "v" });
        back.SecretScanner.IgnoreRules.Should().Equal("r");
        back.SecretScanner.IgnorePatterns.Should().BeEmpty();
    }

    [Fact]
    public void ShouldExcludeMatchesTheRelativePathOrTheName()
    {
        RunProfileExecutor.ShouldExclude("a/b.tmp", ["*.tmp"]).Should().BeTrue();
        RunProfileExecutor.ShouldExclude("a/b.tmp", ["a/*"]).Should().BeTrue();
        RunProfileExecutor.ShouldExclude("a/b.tmp", ["b/*"]).Should().BeFalse();
        RunProfileExecutor.ShouldExclude("a/b.tmp", []).Should().BeFalse();
        RunProfileExecutor.ShouldExclude("B.TMP", ["*.tmp"]).Should().Be(OperatingSystem.IsWindows());
    }

    // Phase 5 decision R: a path holding a lone surrogate is never looked up under the U+FFFD spelling the runtime would give it, so a
    // source naming "\udcff" beside an entry named U+FFFD does not exist, as Python's lookup of byte 0xFF finds nothing.
    [Fact]
    public void ASourcePathHoldingALoneSurrogateIsNeverLookedUpUnderItsReplacementSpelling()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        Write("\ufffd", "replacement");
        Directory.CreateDirectory(Path.Combine(_tmp.FullName, "d\ufffd"));
        Write(Path.Combine("d\ufffd", "a.txt"), "a");
        var lone = Path.Combine(_tmp.FullName, "\udcff");
        var literalGlob = Path.Combine(_tmp.FullName, "d\udcff", "*.txt");
        var ct = TestContext.Current.CancellationToken;

        RunProfileStore.Exists(lone).Should().BeFalse();
        RunProfileStore.IsDirectory(Path.Combine(_tmp.FullName, "d\udcff")).Should().BeFalse();

        var validate = () => RunProfileStore.ValidateProfile(new RunProfile("lone", sources: Sources(lone)));
        validate.Should().Throw<FileNotFoundException>().WithMessage($"Path does not exist: {lone}");
        var collect = () => RunProfileExecutor.CollectMatches(lone, ct);
        collect.Should().Throw<FileNotFoundException>().WithMessage($"Path does not exist: {lone}");
        RunProfileExecutor.CollectMatches(literalGlob, ct).Should().BeEmpty();

        var structured = () => RunProfileExecutor.CollectStructuredMatches(literalGlob, cancellationToken: ct);
        structured.Should().Throw<FileNotFoundException>().WithMessage($"Path does not exist: {literalGlob}");
        var required = new RunProfile("lone-structured", sources: [new RunProfileSource(lone) { Alias = "s" }]);
        var run = () => RunProfileExecutor.ExecuteProfile(required, baseDir: Path.Combine(_tmp.FullName, "out"), cancellationToken: ct);
        run.Should().Throw<FileNotFoundException>().WithMessage($"Path does not exist: {lone}");

        var optional = new RunProfile(
            "lone-optional",
            sources: [new RunProfileSource(lone) { Alias = "s", Optional = true }, new RunProfileSource(literalGlob) { Alias = "g", Optional = true }]);
        var result = RunProfileExecutor.ExecuteProfile(optional, baseDir: Path.Combine(_tmp.FullName, "out"), cancellationToken: ct);
        result.Files.Should().BeEmpty();
        result.Sources.Select(source => (source.Skipped, source.Reason)).Should().Equal((true, "missing"), (true, "no-matches"));

        // A wildcard over names that are valid UTF-8 still matches: the U+FFFD entry is a real name there.
        RunProfileExecutor.CollectMatches(Path.Combine(_tmp.FullName, "d*", "*.txt"), ct).Should().ContainSingle();
    }

    // Decision R: Python's expandvars / expanduser raise UnicodeEncodeError looking up a variable or user name holding a lone surrogate, aborting
    // the run; the port finds no such variable or user, so the path keeps its text and is missing (run_parity.sh python-surrogate-name-lookup).
    [Fact]
    public void AVariableOrUserNameHoldingALoneSurrogateIsNotFoundAndTheSourceIsMissing()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        Write("in.txt", "in");
        var variable = Path.Combine(_tmp.FullName, "${\ud800}", "in.txt");
        var ct = TestContext.Current.CancellationToken;

        var validate = () => RunProfileStore.ValidateProfile(new RunProfile("vars", sources: Sources(variable)));
        validate.Should().Throw<FileNotFoundException>().WithMessage($"Path does not exist: {variable}");
        var collect = () => RunProfileExecutor.CollectMatches(variable, ct);
        collect.Should().Throw<FileNotFoundException>().WithMessage($"Path does not exist: {variable}");

        var optional = new RunProfile(
            "names",
            sources:
            [
                new RunProfileSource(variable) { Alias = "e", Optional = true },
                new RunProfileSource("~\ud800/x.txt") { Alias = "t", Optional = true },
                new RunProfileSource(Path.Combine(_tmp.FullName, "in.txt")) { Alias = "i" },
            ]);
        var result = RunProfileExecutor.ExecuteProfile(optional, baseDir: Path.Combine(_tmp.FullName, "out"), cancellationToken: ct);

        result.Files.Select(file => PathText.RelativePosix(result.OutputDir, file.Destination)).Should().Equal("i/in.txt");
        result.Sources.Select(source => (source.Skipped, source.Reason)).Should().Equal((true, "missing"), (true, "missing"), (false, (string?)null));
    }

    // Plan decision: a structured source never collects the run's own profile directory (profile.json and raw/), which the offline runner
    // writes elsewhere; another profile's directory under the same Profiles root is collected like any other tree.
    [Fact]
    public void AStructuredGlobNeverCollectsTheRunsOwnProfileDirectory()
    {
        Write(Path.Combine("a", "x.txt"), "x");
        Write(Path.Combine("Profiles", "other", "y.txt"), "y");
        var profile = new RunProfile(
            "links",
            sources: [new RunProfileSource(Path.Combine(_tmp.FullName, "*", "x.txt")) { Alias = "files" }, new RunProfileSource(Path.Combine(_tmp.FullName, "*")) { Alias = "dirs" }]);

        var result = RunProfileExecutor.ExecuteProfile(profile, baseDir: _tmp.FullName, timestamp: "20250102T030405Z", cancellationToken: TestContext.Current.CancellationToken);

        result.Files.Select(file => PathText.RelativePosix(result.OutputDir, file.Destination)).Order(StringComparer.Ordinal)
            .Should().Equal("dirs/other/y.txt", "dirs/x.txt", "files/x.txt");
    }
}
