using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Tests.Infrastructure;

/// <summary>Truth table generated from CPython pathlib.PurePosixPath name and suffix.</summary>
public sealed class PathTextTests
{
    [Theory]
    [InlineData(".env", ".env", "")]
    [InlineData(".env.local", ".env.local", ".local")]
    [InlineData("Dockerfile", "Dockerfile", "")]
    [InlineData("web.config", "web.config", ".config")]
    [InlineData("a.b.c", "a.b.c", ".c")]
    [InlineData(".hidden.", ".hidden.", "")]
    [InlineData("..", "..", "")]
    [InlineData(".", "", "")]
    [InlineData("foo.", "foo.", "")]
    [InlineData("", "", "")]
    [InlineData("a/b/", "b", "")]
    [InlineData("dir/.env", ".env", "")]
    [InlineData("x.tar.gz", "x.tar.gz", ".gz")]
    [InlineData("archive.", "archive.", "")]
    [InlineData("a/b.c/", "b.c", ".c")]
    [InlineData("/", "", "")]
    [InlineData("/srv/app/Web.Config", "Web.Config", ".Config")]
    public void NameAndSuffixMatchPathlib(string path, string expectedName, string expectedSuffix)
    {
        PathText.Name(path).Should().Be(expectedName);
        PathText.Suffix(path).Should().Be(expectedSuffix);
    }

    [Fact]
    public void LoweringHelpersUseTheInvariantCulture()
    {
        PathText.NameLower("/srv/app/Web.CONFIG").Should().Be("web.config");
        PathText.SuffixLower("/srv/app/Web.CONFIG").Should().Be(".config");
        PathText.SuffixLower("ISTANBUL.INI").Should().Be(".ini");
    }

    [Fact]
    public void ToPosixAndRelativePosixUseForwardSlashes()
    {
        var root = Path.Combine(Path.GetTempPath(), "driftbuster-root");
        var nested = Path.Combine(root, "etc", "app", "web.config");

        PathText.RelativePosix(root, nested).Should().Be("etc/app/web.config");
        PathText.ToPosix(Path.Combine("a", "b")).Should().Be("a/b");
        PathText.RelativePosix(root, root).Should().Be(".");
    }

    // sorted() over PurePosixPath objects compares component lists, each component by code point.
    [Fact]
    public void ComparePosixPathsMatchesPurePathOrdering()
    {
        string[] paths = ["a-b/x.txt", "a.txt", "a/sub/y.txt", "a/z.txt", "\uFF5E.conf", "\U0001F600.conf", "a", "A.txt"];
        var sorted = paths.OrderBy(path => path, Comparer<string>.Create(PathText.ComparePosixPaths)).ToList();
        sorted.Should().Equal("A.txt", "a", "a/sub/y.txt", "a/z.txt", "a-b/x.txt", "a.txt", "\uFF5E.conf", "\U0001F600.conf");
        PathText.CompareCodePoints("\U0001F600", "\uFF5E").Should().BePositive();
        string.CompareOrdinal("\U0001F600", "\uFF5E").Should().BeNegative();
        PathText.CompareCodePoints("ab", "abc").Should().BeNegative();
        PathText.ComparePosixPaths("a", "a").Should().Be(0);
    }
}
