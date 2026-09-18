using System.Text;

using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Profiles.Run;
using DriftBuster.Backend.Tests.Secrets;

using static DriftBuster.Backend.Tests.Profiles.Run.RunProfilesTests;

namespace DriftBuster.Backend.Tests.Profiles.Run;

/// <summary>
/// Offline runner config shapes run as structured run profile sources: string and object sources, optional and required sources, exclude
/// patterns, recursive globs, symlinks and profile validation.
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

        result.Files.Any(file => LexicalPath.RelativeTo(file.Destination, Path.Combine(result.OutputDir, "missing")) is not null).Should().BeFalse();
        var summary = result.Sources.Should().ContainSingle(entry => entry.Directory == "missing").Subject;
        summary.Skipped.Should().BeTrue();
        summary.Reason.Should().Be("no-matches");
    }

    // An optional mapping source makes the profile structured, so the string source is collected as the offline runner collects it.
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
        act.Should().Throw<InvalidDataException>();

        var source = RunProfile.SourceFromDict(Map(("path", "~/data"), ("alias", "  "), ("exclude", "*.tmp")));
        source.Alias.Should().BeNull();
        source.Exclude.Should().Equal("*.tmp");

        var rootSource = RunProfile.SourceFromDict(Map(("path", "/")));
        RunProfileExecutor.DestinationName(rootSource, fallbackIndex: 7).Should().Be("source_07");

        // A source without an alias is always source_NN; an alias is made safe for a directory name.
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

    // The run profile carries no tags, so the last check is that a tags value is accepted.
    [Fact]
    public void OfflineRunnerProfileValidations()
    {
        FromDict(Map(("name", string.Empty)))
            .Should().Throw<InvalidDataException>().WithMessage("Profile requires a non-empty 'name'.");
        FromDict(Map(("name", "demo"), ("sources", new List<object?> { "/tmp/a" }), ("baseline", "missing")))
            .Should().Throw<InvalidDataException>().WithMessage("Profile baseline must reference one of the declared sources.");
        FromDict(Map(("name", "demo"), ("sources", new List<object?> { "/tmp/a" }), ("options", "invalid")))
            .Should().Throw<InvalidDataException>().WithMessage("Profile 'options' must be a mapping if provided.");
        FromDict(Map(("name", "demo"), ("sources", new List<object?> { "/tmp/a" }), ("secret_scanner", "invalid")))
            .Should().Throw<InvalidDataException>().WithMessage("Profile 'secret_scanner' must be a mapping if provided.");
        FromDict(Map(("name", "demo")))
            .Should().Throw<InvalidDataException>().WithMessage("Profile must define at least one source.");

        var profile = RunProfile.FromOfflineRunnerDict(Map(("name", "tags"), ("sources", new List<object?> { "/tmp/a" }), ("tags", "prod")));
        profile.Name.Should().Be("tags");

        static Func<RunProfile> FromDict(OrderedDictionary<string, object?> payload) => () => RunProfile.FromOfflineRunnerDict(payload);
    }
}
