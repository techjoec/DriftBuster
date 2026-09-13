using DriftBuster.Backend.Diff;

using static DriftBuster.Backend.Tests.Diff.DiffOracleData;

namespace DriftBuster.Backend.Tests.Diff;

/// <summary>
/// <see cref="SequenceMatcher"/> against CPython 3.13 <c>difflib.SequenceMatcher(None, a, b)</c> on the cases in
/// <c>Data/difflib_cases.json</c> (empty sides, ties, adjacent-block collapse, the 199/200/201-line autojunk threshold,
/// the popularity boundary at <c>n // 100 + 1</c>, and 200+ line inputs with repeated lines).
/// </summary>
public sealed class SequenceMatcherTests
{
    private const string DataFile = "difflib_cases.json";

    public static TheoryData<string> CaseNames => Names(DataFile);

    private static (List<string> A, List<string> B, OrderedDictionary<string, object?> Case) Load(string name)
    {
        var entry = Case(DataFile, name);
        return (Strings(entry["a"]), Strings(entry["b"]), entry);
    }

    private static string Render(IEnumerable<DiffOpcode> codes) => string.Join("; ", codes);

    private static string RenderExpected(object? codes)
        => string.Join("; ", List(codes).Select(code => string.Join(' ', List(code).Select(part => part!.ToString()))));

    [Fact]
    public void OracleCoversAtLeastThirtyCases()
    {
        DiffOracleData.Load(DataFile).Should().HaveCountGreaterThan(29);
    }

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void GetOpcodesMatchesDifflib(string name)
    {
        var (a, b, entry) = Load(name);
        Render(new SequenceMatcher(a, b).GetOpcodes()).Should().Be(RenderExpected(entry["opcodes"]));
    }

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void GetMatchingBlocksMatchesDifflib(string name)
    {
        var (a, b, entry) = Load(name);
        var expected = List(entry["matching_blocks"]).Select(block => string.Join(' ', List(block).Select(part => part!.ToString())));
        new SequenceMatcher(a, b).GetMatchingBlocks().Select(block => block.ToString()).Should().Equal(expected);
    }

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void PopularElementsMatchDifflib(string name)
    {
        var (a, b, entry) = Load(name);
        new SequenceMatcher(a, b).Popular.Should().BeEquivalentTo(Strings(entry["popular"]));
    }

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void GetGroupedOpcodesMatchesDifflib(string name)
    {
        var (a, b, entry) = Load(name);
        foreach (var (context, groups) in Map(entry["grouped"]))
        {
            var n = int.Parse(context, System.Globalization.CultureInfo.InvariantCulture);
            var actual = new SequenceMatcher(a, b).GetGroupedOpcodes(n).Select(Render).ToList();
            actual.Should().Equal(List(groups).Select(RenderExpected), "n={0}", n);
        }
    }

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
