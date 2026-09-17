using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Infrastructure.EngineRe;

namespace DriftBuster.Backend.Tests.Infrastructure;

/// <summary>
/// <see cref="EnginePattern"/> against CPython 3.13 on the sample in <c>Data/regex_cases.json</c>: the <c>_sre</c> case
/// tables over the sampled code point windows, the code point set of a sample of single-character atoms over every code point, and
/// <c>finditer</c> / <c>search</c> results (spans, groups, <c>lastindex</c>) or compile errors for the hunt rules, the shipped
/// secret rules and a sample of adversarial patterns.
/// </summary>
public sealed class EngineReCaseTests
{
    internal static readonly Lazy<OrderedDictionary<string, object?>> Data = new(() =>
    {
        var path = Path.Combine(RepoPaths.Root, "gui", "DriftBuster.Backend.Tests", "Infrastructure", "Data", "regex_cases.json");
        EngineJson.TryLoads(File.ReadAllText(path), out var value).Should().BeTrue();
        return (OrderedDictionary<string, object?>)value!;
    });

    public static TheoryData<int> AtomIndexes => Indexes("atoms");

    public static TheoryData<int> PatternIndexes => Indexes("patterns");

    private static TheoryData<int> Indexes(string section)
    {
        var data = new TheoryData<int>();
        for (var index = 0; index < ((List<object?>)Data.Value[section]!).Count; index++)
        {
            data.Add(index);
        }

        return data;
    }

    private static OrderedDictionary<string, object?> Entry(string section, int index)
        => (OrderedDictionary<string, object?>)((List<object?>)Data.Value[section]!)[index]!;

    internal static CodePointSet RangesOf(object? ranges)
        => CodePointSet.FromRanges(((List<object?>)ranges!).Select(range =>
        {
            var pair = (List<object?>)range!;
            return ((int)pair[0]!, (int)pair[1]!);
        }));

    // Every code point inside the sampled windows; the table data covers only these.
    internal static IEnumerable<int> SampledCodePoints()
        => ((List<object?>)Data.Value["windows"]!).Cast<List<object?>>().SelectMany(window => Enumerable.Range((int)window[0]!, (int)window[1]! - (int)window[0]! + 1));

    [Fact]
    public void LowercaseTableMatchesSre()
    {
        var casing = (OrderedDictionary<string, object?>)Data.Value["casing"]!;
        var expected = ((List<object?>)casing["lower"]!).Select(item => (List<object?>)item!).ToDictionary(pair => (int)pair[0]!, pair => (int)pair[1]!);
        var mismatches = new List<string>();
        foreach (var code in SampledCodePoints())
        {
            var want = expected.TryGetValue(code, out var lower) ? lower : code;
            if (EngineCharacterData.Lower(code) != want)
            {
                mismatches.Add($"U+{code:X4}: {EngineCharacterData.Lower(code):X4} != {want:X4}");
            }
        }

        mismatches.Should().BeEmpty();
    }

    [Fact]
    public void IsAlnumMatchesStrIsAlnumOnTheSampledCodePoints()
    {
        var alnum = RangesOf(Data.Value["alnum"]);
        var mismatches = new List<string>();
        foreach (var code in SampledCodePoints())
        {
            if (code is >= 0xD800 and <= 0xDFFF)
            {
                continue;
            }

            var rune = new System.Text.Rune(code);
            if (EngineText.IsAlnum(rune) != alnum.Contains(code) || EngineText.IsWordRune(rune) != (code == '_' || alnum.Contains(code)))
            {
                mismatches.Add($"U+{code:X4}");
            }
        }

        mismatches.Should().BeEmpty();
    }

    [Fact]
    public void IsPrintableMatchesStrIsPrintableOnTheSampledCodePoints()
    {
        var nonPrintable = RangesOf(Data.Value["nonprintable"]);
        var mismatches = new List<string>();
        foreach (var code in SampledCodePoints())
        {
            if (EngineText.IsPrintable(code) == nonPrintable.Contains(code))
            {
                mismatches.Add($"U+{code:X4}");
            }
        }

        mismatches.Should().BeEmpty();
    }

    [Fact]
    public void CasedSetMatchesSre()
    {
        var casing = (OrderedDictionary<string, object?>)Data.Value["casing"]!;
        var expected = RangesOf(casing["cased"]);
        var windows = CodePointSet.FromCodePoints(SampledCodePoints());
        var actual = CodePointSet.FromPredicate(EngineCharacterData.IsCased).Intersect(windows);
        actual.Except(expected).Ranges.Should().BeEmpty();
        expected.Except(actual).Ranges.Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(AtomIndexes))]
    public void AtomMatchesTheSameCodePoints(int index)
    {
        var entry = Entry("atoms", index);
        var pattern = (string)entry["pattern"]!;
        var expected = RangesOf(entry["ranges"]);
        var parsed = ReParser.Parse(pattern, EngineReFlags.None);
        parsed.Nodes.Should().ContainSingle();
        var actual = ReCharacterSets.NodeSet(parsed.Nodes[0], parsed.State.Flags);

        expected.Except(actual).Ranges.Should().BeEmpty($"only the recorded cases match these for {pattern}");
        actual.Except(expected).Ranges.Should().BeEmpty($"only the engine matches these for {pattern}");

        var compiled = EnginePattern.Compile(pattern);
        foreach (var code in SampleCodePoints(expected))
        {
            var spelled = Spell(code);
            (compiled.Match(spelled, TestContext.Current.CancellationToken) is { } match && match.End == spelled.Length).Should().Be(expected.Contains(code), $"U+{code:X4} under {pattern}");
        }
    }

    // Both ends of every expected range and their neighbours, plus fixed probes around the surrogate block and plane edges.
    private static IEnumerable<int> SampleCodePoints(CodePointSet expected)
    {
        var probes = new HashSet<int> { 0, 'a', 'A', '\n', 0xD7FF, 0xD800, 0xDBFF, 0xDC00, 0xDFFF, 0xE000, 0xFFFF, 0x10000, 0x1F600, 0x10FFFF };
        foreach (var (low, high) in expected.Ranges.Take(4000))
        {
            probes.UnionWith([low - 1, low, high, high + 1]);
        }

        return probes.Where(code => code is >= 0 and <= CodePointSet.MaxCodePoint);
    }

    private static string Spell(int code) => code > 0xFFFF ? char.ConvertFromUtf32(code) : ((char)code).ToString();

    [Theory]
    [MemberData(nameof(PatternIndexes))]
    public void PatternBehavesLikeRe(int index)
    {
        var entry = Entry("patterns", index);
        var pattern = (string)entry["pattern"]!;
        var flags = (EngineReFlags)(int)entry["flags"]!;
        if (entry.TryGetValue("error", out var error))
        {
            AssertCompileError(pattern, flags, (OrderedDictionary<string, object?>)error!);
            return;
        }

        var compiled = EnginePattern.Compile(pattern, flags);
        compiled.Groups.Should().Be((int)entry["groups"]!);
        foreach (var run in ((List<object?>)entry["runs"]!).Cast<OrderedDictionary<string, object?>>())
        {
            var text = (string)run["text"]!;
            var expectedIter = ((List<object?>)run["finditer"]!).Select(Render).ToList();
            compiled.FindIter(text, TestContext.Current.CancellationToken).Select(match => Render(compiled, text, match)).Should().Equal(expectedIter, $"finditer of {pattern} over {EngineRepr.StrRepr(text)}");
            var search = compiled.Search(text, TestContext.Current.CancellationToken);
            (search is null ? "None" : Render(compiled, text, search)).Should().Be(run["search"] is null ? "None" : Render(run["search"]));
        }
    }

    private static void AssertCompileError(string pattern, EngineReFlags flags, OrderedDictionary<string, object?> error)
    {
        var compile = () => EnginePattern.Compile(pattern, flags);
        switch ((string)error["type"]!)
        {
            case "OverflowError":
                compile.Should().Throw<OverflowException>().WithMessage((string)error["message"]!);
                break;
            default:
                var thrown = compile.Should().Throw<EngineReException>().Which;
                thrown.Message.Should().Be((string)error["message"]!);
                thrown.Position.Should().Be(error["pos"] is null ? null : (int)error["pos"]!);
                break;
        }
    }

    private static string Render(object? record)
    {
        var map = (OrderedDictionary<string, object?>)record!;
        var span = (List<object?>)map["span"]!;
        var groups = ((List<object?>)map["groups"]!).Select(group => group is null ? "None" : EngineRepr.StrRepr((string)group));
        return $"({span[0]}, {span[1]}) [{string.Join(", ", groups)}] last={map["lastindex"]?.ToString() ?? "None"}";
    }

    private static string Render(EnginePattern pattern, string text, EngineMatch match)
    {
        var groups = Enumerable.Range(0, pattern.Groups + 1).Select(index => match.Group(index) is { } value ? EngineRepr.StrRepr(value) : "None");
        return $"({CodePointOffset(text, match.Start)}, {CodePointOffset(text, match.End)}) [{string.Join(", ", groups)}] last={match.LastIndex?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "None"}";
    }

    private static int CodePointOffset(string text, int utf16Offset) => ReTokenizer.CodePoints(text[..utf16Offset]).Length;

    [Fact]
    public void CasesCoverTheShippedPatterns()
    {
        var patterns = ((List<object?>)Data.Value["patterns"]!).Cast<OrderedDictionary<string, object?>>().Select(entry => (string)entry["pattern"]!).ToList();
        patterns.Should().Contain("AKIA[0-9A-Z]{16}");
    }
}
