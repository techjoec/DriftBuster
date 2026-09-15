using System.Text;

using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Profiles.Run;
using DriftBuster.Backend.Tests.Secrets;

using static DriftBuster.Backend.Tests.Profiles.Run.RunProfilesTests;

namespace DriftBuster.Backend.Tests.Profiles.Run;

/// <summary>
/// The config-shape tests of tests/core/test_offline_runner.py, run against the run profile's structured sources (plan decision: the
/// offline collector profile folds into the run profile). <c>load_config</c> is <see cref="RunProfile.FromDict"/> over the profile
/// mapping, <c>execute_config_path</c> is <see cref="RunProfileExecutor.ExecuteProfile(RunProfile, string?, string?, CancellationToken)"/>,
/// and <c>OfflineCollectionSource.from_dict</c> / <c>destination_name</c> are <see cref="RunProfile.SourceFromDict"/> and
/// <see cref="RunProfileExecutor.DestinationName"/>. The manifest's source summaries are <see cref="ProfileRunResult.Sources"/>. The
/// remaining tests carry the collection and profile-validation tests of the same file over to structured profiles, whose sources the run
/// collects as <c>execute_config</c> does (a string source joins a structured profile to keep the Python inputs).
/// </summary>
[Collection(SecretRuleCacheCollection.Name)]
public sealed class OfflineRunnerSourceTests : IDisposable
{
    private readonly SecretRuleCacheIsolation _isolation = new();
    private readonly DirectoryInfo _tmp = Directory.CreateTempSubdirectory("driftbuster-offline-sources-");

    public void Dispose()
    {
        _isolation.Dispose();
        _tmp.Delete(recursive: true);
    }

    [Fact]
    public void LoadConfigAcceptsStringAndObjectSources()
    {
        var configPayload = Map(
            ("name", "demo"),
            ("sources", new List<object?>
            {
                Path.Combine(_tmp.FullName, "file.txt"),
                Map(("path", Path.Combine(_tmp.FullName, "dir")), ("alias", "dir"), ("optional", true)),
            }));

        var profile = RunProfile.FromDict(configPayload);
        profile.Name.Should().Be("demo");
        profile.Sources.Should().HaveCount(2);
        var (firstSource, secondSource) = (profile.Sources[0], profile.Sources[1]);
        firstSource.Path.Should().EndWith("file.txt");
        firstSource.IsPathOnly.Should().BeTrue();
        secondSource.Alias.Should().Be("dir");
        secondSource.Optional.Should().BeTrue();
    }

    [Fact]
    public void ExecuteOfflineRunHandlesOptionalSource()
    {
        var existing = Path.Combine(_tmp.FullName, "present.log");
        File.WriteAllText(existing, "log", Encoding.UTF8);

        var profile = RunProfile.FromDict(Map(
            ("name", "optional"),
            ("sources", new List<object?>
            {
                Map(("path", existing)),
                Map(("path", Path.Combine(_tmp.FullName, "missing", "*.log")), ("alias", "missing"), ("optional", true)),
            })));

        var result = RunProfileExecutor.ExecuteProfile(profile, baseDir: Path.Combine(_tmp.FullName, "out"), cancellationToken: TestContext.Current.CancellationToken);

        result.Files.Any(file => PythonPurePath.RelativeTo(file.Destination, Path.Combine(result.OutputDir, "missing")) is not null).Should().BeFalse();
        var summary = result.Sources.Should().ContainSingle(entry => entry.Directory == "missing").Subject;
        summary.Skipped.Should().BeTrue();
        summary.Reason.Should().Be("no-matches");
    }

    // Python's string source runs through execute_config; an optional mapping source joins it so the port collects the profile as the
    // offline runner does (a profile of string sources is run as execute_profile).
    [Fact]
    public void ExecuteOfflineRunMissingRequiredSource()
    {
        var missing = Path.Combine(_tmp.FullName, "missing.txt");
        var profile = RunProfile.FromDict(Map(
            ("name", "missing-required"),
            ("sources", new List<object?> { missing, Map(("path", Path.Combine(_tmp.FullName, "other.txt")), ("optional", true)) })));
        profile.IsStructured.Should().BeTrue();

        var act = () => RunProfileExecutor.ExecuteProfile(profile, baseDir: Path.Combine(_tmp.FullName, "out"), cancellationToken: TestContext.Current.CancellationToken);
        act.Should().Throw<FileNotFoundException>().WithMessage($"Path does not exist: {missing}");
    }

    [Fact]
    public void ExecuteOfflineRunRespectsExcludePatterns()
    {
        var dataDir = Directory.CreateDirectory(Path.Combine(_tmp.FullName, "data")).FullName;
        File.WriteAllText(Path.Combine(dataDir, "keep.log"), "keep", Encoding.UTF8);
        File.WriteAllText(Path.Combine(dataDir, "ignore.tmp"), "ignore", Encoding.UTF8);

        var profile = RunProfile.FromDict(Map(
            ("name", "excludes"),
            ("sources", new List<object?> { Map(("path", dataDir), ("exclude", new List<object?> { "*.tmp" })) })));

        var result = RunProfileExecutor.ExecuteProfile(profile, baseDir: Path.Combine(_tmp.FullName, "out"), cancellationToken: TestContext.Current.CancellationToken);

        var paths = result.Files.Select(file => PathText.RelativePosix(result.OutputDir, file.Destination)).ToList();
        paths.Should().AllSatisfy(path => path.Should().NotContain("ignore.tmp"));
        paths.Should().Contain(path => path.EndsWith("keep.log", StringComparison.Ordinal));
    }

    [Fact]
    public void OfflineCollectionSourceValidations()
    {
        var act = () => RunProfile.SourceFromDict(Map());
        act.Should().Throw<PythonValueException>();

        var source = RunProfile.SourceFromDict(Map(("path", "~/data"), ("alias", "  "), ("exclude", "*.tmp")));
        source.Alias.Should().BeNull();
        source.Exclude.Should().Equal("*.tmp");

        var rootSource = RunProfile.SourceFromDict(Map(("path", "/")));
        RunProfileExecutor.DestinationName(rootSource, fallbackIndex: 7).Should().Be("source_07");

        // Plan decision: a source without an alias is always source_NN (Python names a named path after it); an alias is _safe_name(alias).
        RunProfileExecutor.DestinationName(RunProfile.SourceFromDict(Map(("path", "~/data"))), fallbackIndex: 7).Should().Be("source_07");
        RunProfileExecutor.DestinationName(RunProfile.SourceFromDict(Map(("path", "/"), ("alias", "a b"))), fallbackIndex: 7).Should().Be("a-b");
    }

    [Fact]
    public void ExecuteOfflineRunDeduplicatesRecursiveGlobMatches()
    {
        var sourceRoot = Directory.CreateDirectory(Path.Combine(_tmp.FullName, "source")).FullName;
        var nestedDir = Directory.CreateDirectory(Path.Combine(sourceRoot, "nested")).FullName;
        File.WriteAllText(Path.Combine(sourceRoot, "root.log"), "root", Encoding.UTF8);
        File.WriteAllText(Path.Combine(nestedDir, "child.log"), "child", Encoding.UTF8);

        var profile = RunProfile.FromDict(Map(
            ("name", "recursive-glob"),
            ("sources", new List<object?> { Map(("path", $"{sourceRoot}/**/*"), ("alias", "src")) })));

        var result = RunProfileExecutor.ExecuteProfile(profile, baseDir: Path.Combine(_tmp.FullName, "out"), cancellationToken: TestContext.Current.CancellationToken);

        var collectedPaths = result.Files.Select(file => PathText.RelativePosix(result.OutputDir, file.Destination)).ToList();
        collectedPaths.Should().OnlyHaveUniqueItems();
        collectedPaths.Should().Contain(path => path.EndsWith("root.log", StringComparison.Ordinal));
        collectedPaths.Should().Contain(path => path.EndsWith("child.log", StringComparison.Ordinal));
        collectedPaths.Should().BeEquivalentTo("src/root.log", "src/child.log");
    }

    [Fact]
    public void ExecuteConfigOptionalMissingFile()
    {
        var existing = Path.Combine(_tmp.FullName, "present.txt");
        File.WriteAllText(existing, "data", Encoding.UTF8);

        var profile = RunProfile.FromDict(Map(
            ("name", "optional"),
            ("sources", new List<object?> { existing, Map(("path", Path.Combine(_tmp.FullName, "absent.txt")), ("optional", true), ("alias", "missing")) })));

        var result = RunProfileExecutor.ExecuteProfile(profile, baseDir: Path.Combine(_tmp.FullName, "out"), cancellationToken: TestContext.Current.CancellationToken);

        var summary = result.Sources.Should().ContainSingle(entry => entry.Directory == "missing").Subject;
        summary.Skipped.Should().BeTrue();
        summary.Reason.Should().Be("missing");
    }

    [Fact]
    public void ExecuteConfigRequiredGlobWithoutMatches()
    {
        var pattern = Path.Combine(_tmp.FullName, "missing", "*.log");
        var profile = RunProfile.FromDict(Map(
            ("name", "required"),
            ("sources", new List<object?> { Map(("path", pattern), ("alias", "logs")) })));

        var act = () => RunProfileExecutor.ExecuteProfile(profile, baseDir: Path.Combine(_tmp.FullName, "out"), cancellationToken: TestContext.Current.CancellationToken);

        act.Should().Throw<FileNotFoundException>().WithMessage($"Path does not exist: {pattern}");
    }

    [Fact]
    public void ExecuteConfigSkipsSymlink()
    {
        var realFile = Path.Combine(_tmp.FullName, "real.txt");
        File.WriteAllText(realFile, "data", Encoding.UTF8);
        var symlink = Path.Combine(_tmp.FullName, "link.txt");
        File.CreateSymbolicLink(symlink, realFile);

        var profile = RunProfile.FromDict(Map(
            ("name", "symlinks"),
            ("sources", new List<object?> { realFile, Map(("path", symlink), ("alias", "link")) })));

        var result = RunProfileExecutor.ExecuteProfile(profile, baseDir: Path.Combine(_tmp.FullName, "out"), cancellationToken: TestContext.Current.CancellationToken);

        var paths = result.Files.Select(entry => entry.Source).ToList();
        paths.Should().Contain(path => path.EndsWith("real.txt", StringComparison.Ordinal));
        paths.Should().AllSatisfy(path => path.Should().NotBe(symlink));
    }

    [Fact]
    public void ExecuteConfigExcludesSingleFile()
    {
        var filePath = Path.Combine(_tmp.FullName, "secret.txt");
        File.WriteAllText(filePath, "content", Encoding.UTF8);

        var profile = RunProfile.FromDict(Map(
            ("name", "exclude"),
            ("sources", new List<object?> { Map(("path", filePath), ("exclude", new List<object?> { "secret.txt" })) })));

        var result = RunProfileExecutor.ExecuteProfile(profile, baseDir: Path.Combine(_tmp.FullName, "out"), cancellationToken: TestContext.Current.CancellationToken);

        result.Files.Should().AllSatisfy(entry => PathText.Name(entry.Destination).Should().NotBe("secret.txt"));
    }

    // OfflineRunnerProfile.from_dict is RunProfile.FromOfflineRunnerDict, the reader RunProfile.FromDict hands a structured payload, called
    // with Python's inputs. The run profile carries no tags (from_dict only iterates them), so the last check is that "prod" is accepted.
    [Fact]
    public void OfflineRunnerProfileValidations()
    {
        FromDict(Map(("name", string.Empty)))
            .Should().Throw<PythonValueException>().WithMessage("Profile requires a non-empty 'name'.");
        FromDict(Map(("name", "demo"), ("sources", new List<object?> { "/tmp/a" }), ("baseline", "missing")))
            .Should().Throw<PythonValueException>().WithMessage("Profile baseline must reference one of the declared sources.");
        FromDict(Map(("name", "demo"), ("sources", new List<object?> { "/tmp/a" }), ("options", "invalid")))
            .Should().Throw<PythonValueException>().WithMessage("Profile 'options' must be a mapping if provided.");
        FromDict(Map(("name", "demo"), ("sources", new List<object?> { "/tmp/a" }), ("secret_scanner", "invalid")))
            .Should().Throw<PythonValueException>().WithMessage("Profile 'secret_scanner' must be a mapping if provided.");
        FromDict(Map(("name", "demo")))
            .Should().Throw<PythonValueException>().WithMessage("Profile must define at least one source.");

        var profile = RunProfile.FromOfflineRunnerDict(Map(("name", "tags"), ("sources", new List<object?> { "/tmp/a" }), ("tags", "prod")));
        profile.Name.Should().Be("tags");

        static Func<RunProfile> FromDict(OrderedDictionary<string, object?> payload) => () => RunProfile.FromOfflineRunnerDict(payload);
    }

    // execute_config hands build_context the profile's options as the payload holds them: a scalar is no ignore pattern (secret_option_values
    // of a number or bool is empty) and a list's items are patterns; profile.json still holds run_profiles' str() text.
    [Theory]
    [InlineData("5", 1)]
    [InlineData("true", 1)]
    [InlineData("[\"5\"]", 0)]
    public void AStructuredProfileHandsTheRawOptionsToTheSecretContext(string optionJson, int expectedFindings)
    {
        var dataDir = Directory.CreateDirectory(Path.Combine(_tmp.FullName, "data")).FullName;
        File.WriteAllText(Path.Combine(dataDir, "app.ini"), "password=SuperSecret1234 5True\n", Encoding.UTF8);
        PythonJson.TryLoads(optionJson, out var option).Should().BeTrue();

        var profile = RunProfile.FromDict(Map(
            ("name", "options"),
            ("sources", new List<object?> { Map(("path", dataDir), ("alias", "data")) }),
            ("options", Map(("secret_ignore_patterns", option)))));
        var result = RunProfileExecutor.ExecuteProfile(profile, baseDir: Path.Combine(_tmp.FullName, "out"), cancellationToken: TestContext.Current.CancellationToken);

        var findings = (IList<object?>)((IReadOnlyDictionary<string, object?>)result.Secrets!)["findings"]!;
        findings.Should().HaveCount(expectedFindings);
        profile.Options["secret_ignore_patterns"].Should().Be(PythonRepr.Str(option));
    }

    // execute_config iterates the sources in declared order, so the first glob that matches nothing is the one reported even when the
    // baseline is declared after it.
    [Fact]
    public void AStructuredProfileCollectsSourcesInDeclaredOrder()
    {
        var first = Path.Combine(_tmp.FullName, "none1", "*.txt");
        var second = Path.Combine(_tmp.FullName, "none2", "*.txt");
        var profile = RunProfile.FromDict(Map(
            ("name", "order"),
            ("sources", new List<object?> { Map(("path", first), ("alias", "one")), Map(("path", second), ("alias", "two")) }),
            ("baseline", second)));

        var act = () => RunProfileExecutor.ExecuteProfile(profile, baseDir: Path.Combine(_tmp.FullName, "out"), cancellationToken: TestContext.Current.CancellationToken);

        act.Should().Throw<FileNotFoundException>().WithMessage($"Path does not exist: {first}");
    }

}
