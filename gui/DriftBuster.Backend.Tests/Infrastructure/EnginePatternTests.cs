using DriftBuster.Backend.Infrastructure.EngineRe;

namespace DriftBuster.Backend.Tests.Infrastructure;

/// <summary><see cref="EnginePattern"/> API behaviour: match anchoring, groups, cancellation, stack depth and linear work on pathological lines.</summary>
public sealed class EnginePatternTests
{
    [Fact]
    public void MatchIsAnchoredAtTheStart()
    {
        var pattern = EnginePattern.Compile("b+");

        pattern.Match("abb", TestContext.Current.CancellationToken).Should().BeNull();
        pattern.Match("bba", TestContext.Current.CancellationToken)!.Value.Should().Be("bb");
        pattern.Search("abb", TestContext.Current.CancellationToken)!.Start.Should().Be(1);
    }

    [Fact]
    public void GroupAccessorsFollowReMatch()
    {
        var pattern = EnginePattern.Compile("(?P<word>a)|(b)");
        var match = pattern.Search("b", TestContext.Current.CancellationToken)!;

        pattern.Groups.Should().Be(2);
        pattern.GroupIndex.Should().ContainKey("word").WhoseValue.Should().Be(1);
        match.Group(1).Should().BeNull();
        match.Group(2).Should().Be("b");
        match.GroupStart(1).Should().Be(-1);
        match.GroupStart(2).Should().Be(0);
        match.GroupStart(0).Should().Be(0);
        match.LastIndex.Should().Be(2);
        match.End.Should().Be(1);
        var outOfRange = () => match.Group(3);
        outOfRange.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void CatastrophicBacktrackingStopsWhenCancelled()
    {
        var pattern = EnginePattern.Compile("(x+x+)+y");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(100));

        var search = () => pattern.Search(new string('x', 64), cancellation.Token);
        var findIter = () => pattern.FindIter(new string('x', 64), cancellation.Token).ToList();

        search.Should().Throw<OperationCanceledException>();
        findIter.Should().Throw<OperationCanceledException>();
    }

    [Fact]
    public void ACancelledTokenStopsEveryEntryPointBeforeMatching()
    {
        var pattern = EnginePattern.Compile("a");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        pattern.Invoking(p => p.Match("a", cancellation.Token)).Should().Throw<OperationCanceledException>();
        pattern.Invoking(p => p.Search("a", cancellation.Token)).Should().Throw<OperationCanceledException>();
        pattern.Invoking(p => p.FindIter("a", cancellation.Token).ToList()).Should().Throw<OperationCanceledException>();
    }

    // Run on a dedicated 256 KiB thread, not whichever pool thread (and stack depth) the runner hands the test under load: a
    // matcher that recursed per repetition would overflow it on every run, never only sometimes.
    [Fact]
    public void LongSubjectsDoNotExhaustTheThreadStack()
    {
        var subject = new string('a', 300_000) + "c";
        var token = TestContext.Current.CancellationToken;

        var (end, group) = StackProbe.RunOnSmallStack(() => (
            EnginePattern.Compile("(?:a|b)*c").Match(subject, token)!.End,
            EnginePattern.Compile("(a|b)*?c").Search(subject, token)!.Group(1)));

        end.Should().Be(subject.Length);
        group.Should().Be("a");
    }

    // The shipped feature-flag hunt rule on one line just under the 128 KiB sample: ~10000 `key="flag"` openings, each followed
    // by [^\n]* to the end of the line and a backtrack over every later 'v' that never starts "value=". Backtracking that
    // re-scanned the line and re-entered each tail for every opening did ~10^9 units of work; reusing
    // the run end, the member scan and the tail failures keeps it linear in the line length.
    [Theory]
    [InlineData("key=\"flag\" v ")]
    [InlineData("key=\"flag\" ")]
    [InlineData("<add key=\"featureToggle\" data=\"1\"/>")]
    public void TheFeatureFlagRuleDoesLinearWorkOnAPathologicalLine(string unit)
    {
        const string featureFlag = """key\s*=\s*['"][^'\"]*(feature|flag|toggle)[^'\"]*['"][^\n]*value\s*=\s*['"][^'\"]+['"]""";
        var line = string.Concat(Enumerable.Repeat(unit, ((128 * 1024) / unit.Length) + 1))[..((128 * 1024) - 1)];
        var pattern = EnginePattern.Compile(featureFlag, EngineReFlags.IgnoreCase | EngineReFlags.Multiline);
        var matcher = new ReMatcher(pattern.Program, TestContext.Current.CancellationToken);
        matcher.Reset(line.Select(ch => (int)ch).ToArray(), 0);

        matcher.ScannerSearch(0, mustAdvance: false).Should().BeFalse();

        matcher.Work.Should().BeLessThan(64L * line.Length);
    }

    // The feature-flag element pattern retries its tail (\b, a group of alternatives, quotes) from every position [^>]* backs
    // off to, for every "<feature" opening: quadratic when retried. The tail's outcome depends only on its position, so the
    // positions it failed from are reused across starts and the work stays linear in the line length.
    [Theory]
    [InlineData("<feature enabled=x ")]
    [InlineData("<feature value ")]
    public void TheFeatureElementRuleDoesLinearWorkOnAPathologicalLine(string unit)
    {
        const string featureElement = """<feature\b[^>]*\b(enabled|value)\s*=\s*['"][^'\"]+['"]""";
        var line = string.Concat(Enumerable.Repeat(unit, ((128 * 1024) / unit.Length) + 1))[..((128 * 1024) - 1)];
        var pattern = EnginePattern.Compile(featureElement, EngineReFlags.IgnoreCase | EngineReFlags.Multiline);
        var matcher = new ReMatcher(pattern.Program, TestContext.Current.CancellationToken);
        matcher.Reset(line.Select(ch => (int)ch).ToArray(), 0);

        matcher.ScannerSearch(0, mustAdvance: false).Should().BeFalse();

        matcher.Work.Should().BeLessThan(64L * line.Length);
    }
}
