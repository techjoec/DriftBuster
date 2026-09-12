using System.Diagnostics;
using System.Text.RegularExpressions;

using DriftBuster.Backend.Detection.Plugins;
using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Tests.Infrastructure;

/// <summary>
/// The \G-anchored patterns driven from each line start yield exactly the ^-anchored MULTILINE match set, for
/// every pattern each phase 2 plugin hands to the driver, and do so in linear time over whitespace runs.
/// </summary>
public sealed class LineStartMatcherTests
{
    public static TheoryData<string, string> PluginPatterns()
    {
        var data = new TheoryData<string, string>();
        foreach (var (plugin, pattern) in Patterns())
        {
            foreach (var text in AdversarialTexts)
            {
                data.Add(plugin + ":" + pattern.ToString(), text);
            }
        }

        return data;
    }

    private static IEnumerable<(string Plugin, Regex Pattern)> Patterns() =>
    [
        ("ini", IniPlugin.SectionPattern),
        ("ini", IniPlugin.KeyValuePattern),
        ("conf", ConfPlugin.LogstashBlockPattern),
        ("toml", TomlPlugin.TableHeaderPattern),
        ("toml", TomlPlugin.ArrayOfTablesPattern),
        ("toml", TomlPlugin.KeyEqualsPattern),
        ("hcl", HclPlugin.KeyValuePattern),
    ];

    private static readonly string[] AdversarialTexts =
    [
        "",
        "\n",
        "  ",
        "\n\n\n",
        "k=v\\\n\n\n[s]\nj=w",
        "[s]\n\n\nk",
        "[a]\n\n  \n\t[b]  \n\n",
        "[[a.b]]\n\n [[c]] \n[[d]]x\n[[e]]\n",
        "\n\n\nexport\n\nX=1\n",
        "a\n\n\n\nb=c\n\n\n",
        "k=v\rj=w\r[s]\r",
        "\x1c\x1d\x1e\x1f[s]\x1f\n\x1ck=v\x1c\n",
        " [s] \n\u2028[t]\n\u0085k=v",
        "k\n\n\n=v\n",
        "=v\n:\n[]\n[ ]\n",
        "k=v \\ \n\nl=m\\\n",
        "k = \n\nl = m\n = n\n",
        "input {\n\n filter{\n output\n{\ninput\t{\n",
        "\n \n\n input {\n\n\n output {\n",
        "inputs {\nfilter {\n",
        "a.b-c = \"x\"\n\n\n\n  d = [1,\n 2]\n",
        "k = v\n\n\n\n\n\n\n\n",
        "k =\n v\n",
        "  \r\n  [x]  \r\n",
        "\r[a]\r\n",
        "\u2028k = v\n\u2029[t]\n",
        "\U0001F600 = 1\nk = \U0001F600\n",
        "[s\u0130]\nk\u0130=v\n",
    ];

    [Theory]
    [MemberData(nameof(PluginPatterns))]
    public void DriverYieldsTheAnchoredMultilineMatchSet(string pluginAndPattern, string text)
    {
        var pattern = Patterns().Single(entry => string.Equals(entry.Plugin + ":" + entry.Pattern.ToString(), pluginAndPattern, StringComparison.Ordinal)).Pattern;
        pattern.ToString().Should().StartWith(@"\G");
        var anchored = new Regex("^" + pattern.ToString()[2..], pattern.Options, TimeSpan.FromSeconds(2));

        var expected = anchored.Matches(text).Select(Describe).ToList();
        LineStartMatcher.Matches(pattern, text).Select(Describe).Should().Equal(expected, pattern.ToString());
        LineStartMatcher.IsMatch(pattern, text).Should().Be(expected.Count > 0, pattern.ToString());
    }

    private static string Describe(Match match)
        => $"{match.Index}+{match.Length}:" + string.Join("|", match.Groups.Keys.Select(key => $"{key}={(match.Groups[key].Success ? match.Groups[key].Value : "<none>")}"));

    [Theory]
    [InlineData(50000, "\n", "[s]\nk = v\n")]
    [InlineData(30000, "    \n", "[s]\nk = v\n")]
    [InlineData(30000, "\t \u3000\n", "input {\n")]
    public void WhitespaceRunsAreDrivenInLinearTime(int lines, string line, string tail)
    {
        var text = string.Concat(Enumerable.Repeat(line, lines)) + tail;
        var started = Stopwatch.StartNew();
        foreach (var (_, pattern) in Patterns())
        {
            LineStartMatcher.Matches(pattern, text).Should().HaveCountLessThan(3);
            _ = LineStartMatcher.IsMatch(pattern, text);
        }

        started.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));
    }

    [Theory]
    [InlineData("", 0, 0)]
    [InlineData("ab", 0, 0)]
    [InlineData("ab", 1, 3)]
    [InlineData("ab", 2, 3)]
    [InlineData("ab", 3, 3)]
    [InlineData("a\nb", 1, 2)]
    [InlineData("a\nb", 2, 2)]
    [InlineData("a\nb", 3, 4)]
    [InlineData("a\n", 2, 2)]
    [InlineData("a\r\nb", 2, 3)]
    public void NextLineStartIsTheFirstMultilineAnchorAtOrAfterTheOffset(string text, int offset, int expected)
    {
        LineStartMatcher.NextLineStart(text, offset).Should().Be(expected);
    }
}
