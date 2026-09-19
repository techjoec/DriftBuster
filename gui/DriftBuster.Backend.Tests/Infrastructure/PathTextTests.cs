using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Tests.Infrastructure;

/// <summary>Path name and suffix rules (<c>.env</c> has no suffix, <c>.env.local</c> has <c>.local</c>) and path ordering.</summary>
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
    [InlineData("a/./", "a", "")]
    [InlineData("dir/file.config/.", "file.config", ".config")]
    [InlineData("./", "", "")]
    [InlineData("/.", "", "")]
    [InlineData("a/./.", "a", "")]
    [InlineData("./.", "", "")]
    [InlineData("a/.x", ".x", "")]
    [InlineData("a/..", "..", "")]
    [InlineData(".//", "", "")]
    public void NameAndSuffixMatchPathlib(string path, string expectedName, string expectedSuffix)
    {
        PathText.Name(path).Should().Be(expectedName);
        PathText.Suffix(path).Should().Be(expectedSuffix);
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

    // POSIX paths order by their component lists, each component ordinally, so a folder's entries stay together.
    [Fact]
    public void ComparePosixPathsOrdersByComponent()
    {
        string[] paths = ["a-b/x.txt", "a.txt", "a/sub/y.txt", "a/z.txt", "a", "A.txt"];
        var sorted = paths.OrderBy(path => path, Comparer<string>.Create(PathText.ComparePosixPaths)).ToList();
        sorted.Should().Equal("A.txt", "a", "a/sub/y.txt", "a/z.txt", "a-b/x.txt", "a.txt");
        PathText.ComparePosixPaths("a", "a").Should().Be(0);
    }
}
