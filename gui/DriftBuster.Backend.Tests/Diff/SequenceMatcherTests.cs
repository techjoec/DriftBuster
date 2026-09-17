using DriftBuster.Backend.Diff;

namespace DriftBuster.Backend.Tests.Diff;

/// <summary><see cref="SequenceMatcher"/> autojunk and longest-match selection.</summary>
public sealed class SequenceMatcherTests
{
    [Fact]
    public void AutojunkDisabledKeepsPopularElementsIndexed()
    {
        var b = Enumerable.Repeat("}", 150).Concat(Enumerable.Range(0, 60).Select(i => $"v{i}")).ToList();
        new SequenceMatcher(["x", "}"], b, autojunk: false).Popular.Should().BeEmpty();
        new SequenceMatcher(["x", "}"], b).Popular.Should().Equal("}");
        new SequenceMatcher(["x", "}"], b, autojunk: false).GetMatchingBlocks()[0].Should().Be(new MatchingBlock(1, 0, 1));
    }

    [Fact]
    public void FindLongestMatchPrefersEarliestInAThenB()
    {
        // difflib docs: find_longest_match(0, 5, 0, 9) on " abcd" / "abcd abcd" is Match(a=0, b=4, size=5).
        var a = " abcd".Select(ch => ch.ToString()).ToList();
        var b = "abcd abcd".Select(ch => ch.ToString()).ToList();
        new SequenceMatcher(a, b).FindLongestMatch(0, 5, 0, 9).Should().Be(new MatchingBlock(0, 4, 5));
    }
}
