using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Tests.Infrastructure;

/// <summary>
/// <see cref="PythonGlob"/> against CPython 3.13 <c>Path(root).glob(pattern)</c>: the expected lists were produced by the
/// interpreter over the same tree (sorted, as the hunt and detector walks sort them). run_parity.sh hunt compares the same
/// selectors end to end.
/// </summary>
public sealed class PythonGlobTests : IDisposable
{
    private readonly DirectoryInfo _tmp = Directory.CreateTempSubdirectory("driftbuster-glob-");

    public PythonGlobTests()
    {
        Directory.CreateDirectory(Path.Combine(_tmp.FullName, "sub", "deep"));
        Directory.CreateDirectory(Path.Combine(_tmp.FullName, "real"));
        File.WriteAllText(Path.Combine(_tmp.FullName, "a.txt"), "a");
        File.WriteAllText(Path.Combine(_tmp.FullName, "b.ini"), "b");
        File.WriteAllText(Path.Combine(_tmp.FullName, "sub", "x.ini"), "x");
        File.WriteAllText(Path.Combine(_tmp.FullName, "sub", "deep", "y.txt"), "y");
        File.WriteAllText(Path.Combine(_tmp.FullName, "real", "r.ini"), "r");
        if (!OperatingSystem.IsWindows())
        {
            File.CreateSymbolicLink(Path.Combine(_tmp.FullName, "linkdir"), "real");
        }
    }

    public void Dispose() => _tmp.Delete(recursive: true);

    private List<string> Relative(string pattern)
        => PythonPath.SortedGlob(_tmp.FullName, pattern).Select(path => path[(_tmp.FullName.Length + 1)..]).ToList();

    [Fact]
    public void WildcardsFollowGlobTranslate()
    {
        Relative("?.txt").Should().Equal("a.txt");
        Relative("[ab].*").Should().Equal("a.txt", "b.ini");
        Relative("[!a].*").Should().Equal("b.ini");
        Relative("sub//x.ini").Should().Equal("sub/x.ini");
        Relative("sub/../a.txt").Should().Equal("sub/../a.txt");
    }

    [Fact]
    public void RecursivePartsYieldDirectoriesAndTrailingSeparatorsSelectDirectories()
    {
        Relative("sub/**").Should().Equal("sub", "sub/deep", "sub/deep/y.txt", "sub/x.ini");
        Relative("sub/").Should().Equal("sub");
        Relative("a.txt/").Should().BeEmpty();
    }

    [Fact]
    public void SymlinkedDirectoriesAreFollowedByWildcardPartsOnly()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        Relative("*/*.ini").Should().Equal("linkdir/r.ini", "real/r.ini", "sub/x.ini");
        Relative("**/*.ini").Should().Equal("b.ini", "real/r.ini", "sub/x.ini");
        Relative("linkdir/**").Should().Equal("linkdir", "linkdir/r.ini");
    }

    [Fact]
    public void MissingRootsYieldNothingAndBadPatternsRaise()
    {
        PythonPath.SortedGlob(Path.Combine(_tmp.FullName, "missing"), "**/*", TestContext.Current.CancellationToken).Should().BeEmpty();

        var empty = () => PythonGlob.Glob(_tmp.FullName, string.Empty);
        var dot = () => PythonGlob.Glob(_tmp.FullName, "./");
        var anchored = () => PythonGlob.Glob(_tmp.FullName, "/abs/*");

        empty.Should().Throw<ArgumentException>().WithMessage("Unacceptable pattern: PosixPath('.')");
        dot.Should().Throw<ArgumentException>().WithMessage("Unacceptable pattern: PosixPath('.')");
        anchored.Should().Throw<NotSupportedException>().WithMessage("Non-relative patterns are unsupported");
    }

    [Fact]
    public void ACancelledTokenStopsTheWalk()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        var glob = () => PythonPath.SortedGlob(_tmp.FullName, "**/*.nomatch", cancelled.Token);

        glob.Should().Throw<OperationCanceledException>();
    }
}
