using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Models;
using DriftBuster.Backend.Profiles.Run;
using DriftBuster.Backend.Tests.Secrets;

namespace DriftBuster.Backend.Tests.Profiles.Run;

/// <summary>
/// What a run collects at its own output and at names the runtime cannot spell: a structured source whose only matches are the run's own
/// output is missing (the offline runner never creates that tree), and a listed Linux name that is not UTF-8 is never read under the
/// U+FFFD spelling the runtime gives it (<c>tools/parity/expected_divergences.md</c>, "Run profiles" and "Platform limits").
/// </summary>
[Collection(SecretRuleCacheCollection.Name)]
public sealed class RunProfileCollectionLimitsTests : IDisposable
{
    private readonly SecretRuleCacheIsolation _isolation = new();
    private readonly DirectoryInfo _tmp = Directory.CreateTempSubdirectory("driftbuster-run-profile-limits-");

    public void Dispose()
    {
        _isolation.Dispose();
        SpecialFiles.DeleteTree(_tmp);
    }

    private string Write(string relative, string content)
    {
        var path = Path.Combine(_tmp.FullName, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private ProfileRunResult Run(string name, params RunProfileSource[] sources)
        => Run(_tmp.FullName, name, sources);

    private static ProfileRunResult Run(string baseDir, string name, params RunProfileSource[] sources)
        => RunProfileExecutor.ExecuteProfile(new RunProfile(name, sources: sources), baseDir: baseDir, timestamp: "run", cancellationToken: TestContext.Current.CancellationToken);

    private static IEnumerable<string> Collected(ProfileRunResult result)
        => result.Files.Select(file => PathText.RelativePosix(result.OutputDir, file.Destination)).Order(StringComparer.Ordinal);

    private RunProfileSource Data() => new(Path.Combine(_tmp.FullName, "data")) { Alias = "d" };

    [Fact]
    public void ARequiredGlobMatchingOnlyTheProfilesRootTheRunCreatedIsMissing()
    {
        Write(Path.Combine("data", "x.txt"), "x");
        var pattern = Path.Combine(_tmp.FullName, "Prof*");

        var run = () => Run("own", Data(), new RunProfileSource(pattern) { Alias = "p" });

        run.Should().Throw<FileNotFoundException>().WithMessage($"Path does not exist: {pattern}");
    }

    [Fact]
    public void ARequiredGlobMatchingOnlyTheRunsOwnProfileDirectoryIsMissing()
    {
        Write(Path.Combine("data", "x.txt"), "x");
        Write(Path.Combine("Profiles", "other", "y.txt"), "y");
        var pattern = Path.Combine(_tmp.FullName, "Profiles", "ow*", "raw");

        var run = () => Run("own", Data(), new RunProfileSource(pattern) { Alias = "p" });

        run.Should().Throw<FileNotFoundException>().WithMessage($"Path does not exist: {pattern}");
    }

    [Fact]
    public void AnOptionalSourceMatchingOnlyTheRunsOwnOutputIsSkipped()
    {
        Write(Path.Combine("data", "x.txt"), "x");
        var glob = Path.Combine(_tmp.FullName, "Prof*");
        var literal = Path.Combine(_tmp.FullName, "Profiles", "own");

        var result = Run("own", Data(), new RunProfileSource(glob) { Alias = "p", Optional = true }, new RunProfileSource(literal) { Alias = "q", Optional = true });

        Collected(result).Should().Equal("d/x.txt");
        result.Sources.Skip(1).Select(source => (source.Skipped, source.Reason)).Should().Equal((true, "no-matches"), (true, "missing"));
    }

    [Fact]
    public void AProfilesRootThatExistedBeforeTheRunStillMatches()
    {
        Write(Path.Combine("data", "x.txt"), "x");
        Write(Path.Combine("Profiles", "other", "y.txt"), "y");

        var result = Run("own", Data(), new RunProfileSource(Path.Combine(_tmp.FullName, "Prof*")) { Alias = "p" });

        Collected(result).Should().Equal("d/x.txt", "p/other/y.txt");
    }

    [Fact]
    public void ABaseDirectoryTheRunCreatedIsItsOwnOutputToo()
    {
        Write(Path.Combine("data", "x.txt"), "x");
        var pattern = Path.Combine(_tmp.FullName, "ou*");

        var run = () => Run(Path.Combine(_tmp.FullName, "out"), "own", Data(), new RunProfileSource(pattern) { Alias = "p" });

        run.Should().Throw<FileNotFoundException>().WithMessage($"Path does not exist: {pattern}");
    }

    // dir/ holds <0xFF> ("ff") and U+FFFD ("fffd"); the runtime lists both as U+FFFD, which names only the second.
    private string UndecodableBesideReplacementName()
    {
        var directory = Path.Combine(_tmp.FullName, "dir");
        SpecialFiles.Shell("mkdir \"$1\" && printf 'ff' > \"$1/$ff\" && printf 'fffd' > \"$1/$fffd\" && printf 'x' > \"$1/x\"", directory);
        return directory;
    }

    [Fact]
    public void AGlobListsAnUndecodableNameNeitherAsItselfNorAsItsReplacementSibling()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "file names are bytes only on Linux here");
        var directory = UndecodableBesideReplacementName();
        var pattern = Path.Combine(directory, "*");

        var plain = Run("plain", new RunProfileSource(pattern));
        var structured = Run("structured", new RunProfileSource(pattern) { Alias = "a" });

        Collected(plain).Should().Equal("source_00/x", "source_00/\uFFFD");
        plain.Files.Single(file => file.Destination.EndsWith('\uFFFD')).Size.Should().Be(4);
        Collected(structured).Should().Equal("a/x", "a/\uFFFD");
        structured.Files.Single(file => file.Destination.EndsWith('\uFFFD')).Size.Should().Be(4);
    }

    [Fact]
    public void ADirectoryWalkSkipsAnUndecodableNameAndReadsItsReplacementSiblingOnce()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "file names are bytes only on Linux here");
        var directory = UndecodableBesideReplacementName();
        var alone = Path.Combine(_tmp.FullName, "alone");
        SpecialFiles.Shell("mkdir \"$1\" && printf 'ff' > \"$1/$ff\" && printf 'x' > \"$1/x\"", alone);

        var plain = Run("plain", new RunProfileSource(directory), new RunProfileSource(alone));
        var structured = Run("structured", new RunProfileSource(directory) { Alias = "a" }, new RunProfileSource(alone) { Alias = "b" });

        Collected(plain).Should().Equal("source_00/x", "source_00/\uFFFD", "source_01/x");
        Collected(structured).Should().Equal("a/x", "a/\uFFFD", "b/x");
        structured.Files.Where(file => file.Destination.EndsWith('\uFFFD')).Select(file => file.Size).Should().Equal(4L);
    }
}
