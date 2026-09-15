using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Tests.Infrastructure;

/// <summary>
/// <see cref="PythonPurePath"/> against CPython 3.13 <c>PurePosixPath</c> on the <c>pure_path</c> section of
/// <c>Data/python_regex_cases.json</c>: <c>match</c> (bracket expressions, hidden names, anchors, <c>**</c> as a plain
/// wildcard, astral names) and <c>parts</c> / <c>str</c> / <c>parent</c> / <c>relative_to</c>. The oracle is posix, so the
/// comparison runs on posix hosts.
/// </summary>
public sealed class PythonPurePathTests
{
    private static readonly Lazy<OrderedDictionary<string, object?>> Data = new(() =>
    {
        var path = Path.Combine(RepoPaths.Root, "gui", "DriftBuster.Backend.Tests", "Infrastructure", "Data", "python_regex_cases.json");
        PythonJson.TryLoads(File.ReadAllText(path), out var value).Should().BeTrue();
        return (OrderedDictionary<string, object?>)((OrderedDictionary<string, object?>)value!)["pure_path"]!;
    });

    private static IEnumerable<OrderedDictionary<string, object?>> Section(string name)
        => ((List<object?>)Data.Value[name]!).Cast<OrderedDictionary<string, object?>>();

    [Fact]
    public void MatchAgreesWithPurePosixPath()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        foreach (var entry in Section("match"))
        {
            var path = (string)entry["path"]!;
            var pattern = (string)entry["pattern"]!;
            var match = () => PythonPurePath.Match(path, pattern);
            if (entry.TryGetValue("error", out var error))
            {
                match.Should().Throw<ArgumentException>().Which.Message.Should().StartWith((string)error!);
                continue;
            }

            match().Should().Be((bool)entry["result"]!, $"{PythonRepr.StrRepr(path)}.match({PythonRepr.StrRepr(pattern)})");
        }
    }

    [Fact]
    public void PartsStrParentAndRelativeToAgreeWithPurePosixPath()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        foreach (var entry in Section("paths"))
        {
            var path = (string)entry["path"]!;
            PythonPurePath.Parts(path).Should().Equal(((List<object?>)entry["parts"]!).Cast<string>(), path);
            PythonPurePath.Str(path).Should().Be((string)entry["str"]!, path);
            PythonPurePath.Parent(path).Should().Be((string)entry["parent"]!, path);
            PythonPurePath.RelativeTo(path, (string)entry["other"]!).Should().Be((string?)entry["relative_to"], path);
        }
    }

    // str(PurePosixPath(value)) from CPython 3.13; posix on every host.
    [Theory]
    [InlineData("", ".")]
    [InlineData(".", ".")]
    [InlineData("./", ".")]
    [InlineData("a//b/./c/", "a/b/c")]
    [InlineData("/", "/")]
    [InlineData("//", "//")]
    [InlineData("///x", "/x")]
    [InlineData("//x/y", "//x/y")]
    [InlineData("path\\file.txt", "path\\file.txt")]
    [InlineData("../a/..", "../a/..")]
    [InlineData("a/.", "a")]
    public void PosixStrAgreesWithPurePosixPath(string path, string expected)
        => PythonPurePath.PosixStr(path).Should().Be(expected);

    [Fact]
    public void JoinSpellsTheChildPath()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        PythonPurePath.Join("/a/b/", "c/d.txt").Should().Be("/a/b/c/d.txt");
        PythonPurePath.Join(".", "c").Should().Be("c");
        PythonPurePath.Join("/", "c").Should().Be("/c");
        PythonPurePath.Join("a//b", "c").Should().Be("a/b/c");
    }
}
