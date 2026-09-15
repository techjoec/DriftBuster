using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Tests.Infrastructure;

/// <summary><see cref="PythonFnmatch"/> against CPython 3.13 <c>fnmatch.translate</c> and <c>fnmatch.fnmatchcase</c>.</summary>
public sealed class PythonFnmatchTests
{
    [Theory]
    [InlineData("configs/*.json", "(?s:configs/.*\\.json)\\Z")]
    [InlineData("a*b*c?[!x]", "(?s:a(?>.*?b).*c.[^x])\\Z")]
    [InlineData("*", "(?s:.*)\\Z")]
    [InlineData("**", "(?s:.*)\\Z")]
    [InlineData("[a-c]*", "(?s:[a-c].*)\\Z")]
    [InlineData("x[", "(?s:x\\[)\\Z")]
    [InlineData("[]]", "(?s:[]])\\Z")]
    [InlineData("[!]", "(?s:\\[!\\])\\Z")]
    [InlineData("[z-a]", "(?s:(?!))\\Z")]
    [InlineData("a\\b", "(?s:a\\\\b)\\Z")]
    [InlineData("*.config", "(?s:.*\\.config)\\Z")]
    [InlineData("a[&~|]b", "(?s:a[\\&\\~\\|]b)\\Z")]
    [InlineData("[^x]", "(?s:[\\^x])\\Z")]
    [InlineData("t*st*", "(?s:t(?>.*?st).*)\\Z")]
    public void TranslateAgreesWithCPython(string pattern, string expected)
        => PythonFnmatch.Translate(pattern).Should().Be(expected);

    [Theory]
    [InlineData("configs/settings.json", "configs/*.json", true)]
    [InlineData("configs/sub/settings.json", "configs/*.json", true)]
    [InlineData("other.json", "configs/*.json", false)]
    [InlineData("Configs/a.json", "configs/*.json", false)]
    [InlineData("abc", "a?c", true)]
    [InlineData("a\nc", "a?c", true)]
    [InlineData("b", "[a-c]", true)]
    [InlineData("d", "[!a-c]", true)]
    [InlineData("x[", "x[", true)]
    [InlineData("]", "[]]", true)]
    [InlineData("test", "t*st*", true)]
    public void FnmatchCaseAgreesWithCPython(string name, string pattern, bool expected)
    {
        PythonFnmatch.FnmatchCase(name, pattern).Should().Be(expected);
        if (!OperatingSystem.IsWindows())
        {
            PythonFnmatch.Fnmatch(name, pattern).Should().Be(expected, "normcase is the identity on posix");
        }
    }
}
