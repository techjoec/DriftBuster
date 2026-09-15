using System.Text;

using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Tests.Infrastructure;

/// <summary>
/// <c>glob.glob(pattern, recursive=True)</c> and the <c>posixpath</c> helpers it and <c>run_profiles</c> use. The expected lists are
/// CPython 3.13's over the same tree (relative patterns under <c>t/</c>, here anchored at a temporary root), sorted because
/// <c>glob.glob</c> returns directory listing order.
/// </summary>
public sealed class PythonModuleGlobTests : IDisposable
{
    private readonly DirectoryInfo _tmp = Directory.CreateTempSubdirectory("driftbuster-module-glob-");

    public PythonModuleGlobTests()
    {
        foreach (var file in new[] { "a.txt", "b.log", ".hidden.txt", "sub/c.txt", "sub/.h/d.txt", "sub/deep/e.txt", ".hdir/x.txt", "sub/deep/.f.txt" })
        {
            var path = Path.Combine(Root, file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "x", Encoding.UTF8);
        }

        if (!OperatingSystem.IsWindows())
        {
            File.CreateSymbolicLink(Path.Combine(Root, "link"), "sub");
            File.CreateSymbolicLink(Path.Combine(Root, "dangling"), "nowhere");
        }
    }

    private string Root => Path.Combine(_tmp.FullName, "t");

    public void Dispose() => _tmp.Delete(recursive: true);

    public static TheoryData<string, string[]> Cases => new()
    {
        { "t/*.txt", ["t/a.txt"] },
        { "t/.*", ["t/.hdir", "t/.hidden.txt"] },
        { "t/**", ["t/", "t/a.txt", "t/b.log", "t/dangling", "t/link", "t/link/c.txt", "t/link/deep", "t/link/deep/e.txt", "t/sub", "t/sub/c.txt", "t/sub/deep", "t/sub/deep/e.txt"] },
        { "t/**/*.txt", ["t/a.txt", "t/link/c.txt", "t/link/deep/e.txt", "t/sub/c.txt", "t/sub/deep/e.txt"] },
        { "t/*/", ["t/link/", "t/sub/"] },
        { "t/s?b/*", ["t/sub/c.txt", "t/sub/deep"] },
        { "t/[ab].*", ["t/a.txt", "t/b.log"] },
        { "t/missing/*", [] },
        { "t/sub/c.txt", ["t/sub/c.txt"] },
        { "t/**/", ["t/", "t/link/", "t/link/deep/", "t/sub/", "t/sub/deep/"] },
        { "t/dangling", ["t/dangling"] },
        { "t/dang*", ["t/dangling"] },
        { "t/*/deep/*", ["t/link/deep/e.txt", "t/sub/deep/e.txt"] },
        { "t/sub/[!c]*", ["t/sub/deep"] },
        { "t/[]", [] },
        { "t/.h*/*", ["t/.hdir/x.txt"] },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void GlobMatchesCPython(string pattern, string[] expected)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var prefix = _tmp.FullName + "/";
        var actual = PythonModuleGlob.Glob(prefix + pattern, recursive: true, TestContext.Current.CancellationToken).Select(path => path[prefix.Length..]).Order(StringComparer.Ordinal);
        actual.Should().Equal(expected);
    }

    [Fact]
    public void ARecursivePatternAtTheStartSkipsTheEmptyMatch()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        PythonModuleGlob.Glob("**", recursive: true, TestContext.Current.CancellationToken).Should().NotContain(string.Empty);
        PythonModuleGlob.Glob(string.Empty, recursive: true, TestContext.Current.CancellationToken).Should().BeEmpty();
    }

    // Phase 5 decision R: a literal part holding a lone surrogate names nothing, although the runtime would reach the U+FFFD entry.
    [Fact]
    public void ALiteralPartHoldingALoneSurrogateMatchesNothing()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var replacement = Path.Combine(Root, "�");
        Directory.CreateDirectory(replacement);
        File.WriteAllText(Path.Combine(replacement, "a.txt"), "x", Encoding.UTF8);
        var prefix = _tmp.FullName + "/";
        var ct = TestContext.Current.CancellationToken;

        PythonModuleGlob.Glob(prefix + "t/\udcff", recursive: true, ct).Should().BeEmpty();
        PythonModuleGlob.Glob(prefix + "t/\udcff/*.txt", recursive: true, ct).Should().BeEmpty();
        PythonModuleGlob.Glob(prefix + "t/\udcff/**", recursive: true, ct).Should().BeEmpty();
        PythonModuleGlob.Glob(prefix + "t/\udcff/", recursive: true, ct).Should().BeEmpty();
        PythonModuleGlob.Glob(prefix + "t/\ud800*", recursive: true, ct).Should().BeEmpty();
        PythonModuleGlob.Glob(prefix + "t/*/a.txt", recursive: true, ct).Select(path => path[prefix.Length..]).Should().Equal("t/�/a.txt");
    }

    [Fact]
    public void WithoutRecursionTwoStarsAreOneWildcard()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var prefix = _tmp.FullName + "/";
        PythonModuleGlob.Glob(prefix + "t/**/c.txt", recursive: false, TestContext.Current.CancellationToken).Select(path => path[prefix.Length..]).Order(StringComparer.Ordinal)
            .Should().Equal("t/link/c.txt", "t/sub/c.txt");
    }

    [Theory]
    [InlineData("", ".")]
    [InlineData("/", "/")]
    [InlineData("//", "//")]
    [InlineData("///", "/")]
    [InlineData("//a/../b", "//b")]
    [InlineData("a/../../b", "../b")]
    [InlineData("/../a", "/a")]
    [InlineData("./a/./b/", "a/b")]
    [InlineData("a//b", "a/b")]
    public void NormPathMatchesPosixpath(string path, string expected) => PythonOsPath.NormPath(path).Should().Be(expected);

    [Theory]
    [InlineData("", "", "")]
    [InlineData("/", "/", "")]
    [InlineData("a", "", "a")]
    [InlineData("a/", "a", "")]
    [InlineData("//a//b", "//a", "b")]
    [InlineData("/a/b/", "/a/b", "")]
    [InlineData("a/b", "a", "b")]
    public void SplitMatchesPosixpath(string path, string head, string tail)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        PythonOsPath.Split(path).Should().Be((head, tail));
    }

    [Theory]
    [InlineData("a", "", "a/")]
    [InlineData("", "b", "b")]
    [InlineData("a/", "b", "a/b")]
    [InlineData("a", "/b", "/b")]
    [InlineData("//", "x", "//x")]
    public void JoinMatchesPosixpath(string path, string name, string expected)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        PythonOsPath.Join(path, name).Should().Be(expected);
    }

    [Fact]
    public void AbsPathJoinsTheWorkingDirectoryAndNormalises()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        PythonOsPath.AbsPath("/a/./b/../c/").Should().Be("/a/c");
        PythonOsPath.AbsPath("x/../y").Should().Be(PythonOsPath.NormPath(Directory.GetCurrentDirectory() + "/y"));
        PythonOsPath.AbsPath(string.Empty).Should().Be(PythonOsPath.NormPath(Directory.GetCurrentDirectory()));
    }
}
