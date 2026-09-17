using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Models;
using DriftBuster.Backend.Profiles.Run;
using DriftBuster.Backend.Tests.Secrets;

namespace DriftBuster.Backend.Tests.Profiles.Run;

/// <summary>
/// A structured source whose only matches are the run's own output is missing: the offline runner never creates that tree.
/// </summary>
[Collection(SecretRuleCacheCollection.Name)]
public sealed class RunProfileCollectionLimitsTests : IDisposable
{
    private readonly SecretRuleCacheIsolation _isolation = new();
    private readonly DirectoryInfo _tmp = Directory.CreateTempSubdirectory("driftbuster-run-profile-limits-");

    public void Dispose()
    {
        _isolation.Dispose();
        _tmp.Delete(recursive: true);
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
}
