using DriftBuster.Backend.Infrastructure.PythonRe;

namespace DriftBuster.Backend.Tests.Infrastructure;

/// <summary><see cref="PythonPattern"/> API behaviour outside the oracle: match anchoring, groups, flags and errors.</summary>
public sealed class PythonPatternTests
{
    [Fact]
    public void MatchIsAnchoredAtTheStart()
    {
        var pattern = PythonPattern.Compile("b+");

        pattern.Match("abb", TestContext.Current.CancellationToken).Should().BeNull();
        pattern.Match("bba", TestContext.Current.CancellationToken)!.Value.Should().Be("bb");
        pattern.Search("abb", TestContext.Current.CancellationToken)!.Start.Should().Be(1);
    }

    [Fact]
    public void GroupAccessorsFollowReMatch()
    {
        var pattern = PythonPattern.Compile("(?P<word>a)|(b)");
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
    public void FlagsIncludeInlineFlagsAndImpliedUnicode()
    {
        PythonPattern.Compile("(?im)x").Flags.Should().Be(PythonReFlags.IgnoreCase | PythonReFlags.Multiline | PythonReFlags.Unicode);
        PythonPattern.Compile("x", PythonReFlags.Ascii).Flags.Should().Be(PythonReFlags.Ascii);
    }

    [Fact]
    public void ValueErrorsForStrPatternsAreArgumentExceptions()
    {
        var locale = () => PythonPattern.Compile("x", PythonReFlags.Locale);
        var both = () => PythonPattern.Compile("x", PythonReFlags.Ascii | PythonReFlags.Unicode);

        locale.Should().Throw<ArgumentException>().WithMessage("cannot use LOCALE flag with a str pattern");
        both.Should().Throw<ArgumentException>().WithMessage("ASCII and UNICODE flags are incompatible");
    }

    [Fact]
    public void CompiledPatternsAreCached()
    {
        PythonPattern.Compile("cache-me", PythonReFlags.IgnoreCase).Should().BeSameAs(PythonPattern.Compile("cache-me", PythonReFlags.IgnoreCase));
    }

    [Fact]
    public void CatastrophicBacktrackingStopsWhenCancelled()
    {
        var pattern = PythonPattern.Compile("(x+x+)+y");
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
        var pattern = PythonPattern.Compile("a");
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
            PythonPattern.Compile("(?:a|b)*c").Match(subject, token)!.End,
            PythonPattern.Compile("(a|b)*?c").Search(subject, token)!.Group(1)));

        end.Should().Be(subject.Length);
        group.Should().Be("a");
    }

    // The shipped feature-flag hunt rule on one line just under the 128 KiB sample: ~10000 `key="flag"` openings, each followed
    // by [^\n]* to the end of the line and a backtrack over every later 'v' that never starts "value=". Backtracking that
    // re-scanned the line and re-entered each tail for every opening did ~10^9 units of work (seconds in CPython too); reusing
    // the run end, the member scan and the tail failures keeps it linear in the line length.
    [Theory]
    [InlineData("key=\"flag\" v ")]
    [InlineData("key=\"flag\" ")]
    [InlineData("<add key=\"featureToggle\" data=\"1\"/>")]
    public void TheFeatureFlagRuleDoesLinearWorkOnAPathologicalLine(string unit)
    {
        const string featureFlag = """key\s*=\s*['"][^'\"]*(feature|flag|toggle)[^'\"]*['"][^\n]*value\s*=\s*['"][^'\"]+['"]""";
        var line = string.Concat(Enumerable.Repeat(unit, ((128 * 1024) / unit.Length) + 1))[..((128 * 1024) - 1)];
        var pattern = PythonPattern.Compile(featureFlag, PythonReFlags.IgnoreCase | PythonReFlags.Multiline);
        var matcher = new ReMatcher(pattern.Program, TestContext.Current.CancellationToken);
        matcher.Reset(line.Select(ch => (int)ch).ToArray(), 0);

        matcher.ScannerSearch(0, mustAdvance: false).Should().BeFalse();

        matcher.Work.Should().BeLessThan(64L * line.Length);
    }

    // The feature-flag element pattern retries its tail (\b, a group of alternatives, quotes) from every position [^>]* backs
    // off to, for every "<feature" opening: quadratic in CPython. The tail's outcome depends only on its position, so the
    // positions it failed from are reused across starts and the work stays linear in the line length.
    [Theory]
    [InlineData("<feature enabled=x ")]
    [InlineData("<feature value ")]
    public void TheFeatureElementRuleDoesLinearWorkOnAPathologicalLine(string unit)
    {
        const string featureElement = """<feature\b[^>]*\b(enabled|value)\s*=\s*['"][^'\"]+['"]""";
        var line = string.Concat(Enumerable.Repeat(unit, ((128 * 1024) / unit.Length) + 1))[..((128 * 1024) - 1)];
        var pattern = PythonPattern.Compile(featureElement, PythonReFlags.IgnoreCase | PythonReFlags.Multiline);
        var matcher = new ReMatcher(pattern.Program, TestContext.Current.CancellationToken);
        matcher.Reset(line.Select(ch => (int)ch).ToArray(), 0);

        matcher.ScannerSearch(0, mustAdvance: false).Should().BeFalse();

        matcher.Work.Should().BeLessThan(64L * line.Length);
    }

    [Fact]
    public void OffsetsAreUtf16IndicesAroundAstralCodePoints()
    {
        var match = PythonPattern.Compile("(.)b").Search("\U0001F600\U0001F601b", TestContext.Current.CancellationToken)!;

        match.Start.Should().Be(2);
        match.End.Should().Be(5);
        match.Group(1).Should().Be("\U0001F601");
        match.GroupStart(1).Should().Be(2);
        var outOfRange = () => match.GroupStart(2);
        outOfRange.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void ExceptionConstructorsCarryMessageAndPosition()
    {
        new PythonReException().Position.Should().BeNull();
        new PythonReException("m").Message.Should().Be("m");
        new PythonReException("m", new InvalidOperationException("inner")).InnerException.Should().BeOfType<InvalidOperationException>();
        new PythonReException("m", position: 3).Position.Should().Be(3);
    }
}
