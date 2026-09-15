using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Infrastructure.PythonRe;

namespace DriftBuster.Backend.Tests.Infrastructure;

/// <summary>
/// <see cref="PythonPattern"/> against CPython 3.13 on <c>Data/python_regex_cases.json</c> (written by
/// <c>tools/parity/gen_regex_cases.py</c>): the <c>_sre</c> case tables over every code point, the code point set of
/// every single-character atom over every code point, and <c>finditer</c> / <c>search</c> results (spans, groups,
/// <c>lastindex</c>) or compile errors for the hunt rules, the shipped secret rules and adversarial patterns.
/// </summary>
/// <remarks>
/// Every comparison is exact over every code point: the port reads the runtime's Unicode 16.0 tables through
/// <see cref="PythonUnicode"/>, which answers as CPython 3.13's Unicode 15.1 tables (<see cref="PythonUnicodeOracleTests"/>).
/// </remarks>
public sealed class PythonReOracleTests
{
    private static readonly Lazy<OrderedDictionary<string, object?>> Data = new(() =>
    {
        var path = Path.Combine(RepoPaths.Root, "gui", "DriftBuster.Backend.Tests", "Infrastructure", "Data", "python_regex_cases.json");
        PythonJson.TryLoads(File.ReadAllText(path), out var value).Should().BeTrue();
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

    private static CodePointSet RangesOf(object? ranges)
        => CodePointSet.FromRanges(((List<object?>)ranges!).Select(range =>
        {
            var pair = (List<object?>)range!;
            return ((int)pair[0]!, (int)pair[1]!);
        }));

    [Fact]
    public void LowercaseTableMatchesSre()
    {
        var casing = (OrderedDictionary<string, object?>)Data.Value["casing"]!;
        var expected = ((List<object?>)casing["lower"]!).Select(item => (List<object?>)item!).ToDictionary(pair => (int)pair[0]!, pair => (int)pair[1]!);
        var mismatches = new List<string>();
        for (var code = 0; code <= CodePointSet.MaxCodePoint; code++)
        {
            var want = expected.TryGetValue(code, out var lower) ? lower : code;
            if (PythonCharacterData.Lower(code) != want)
            {
                mismatches.Add($"U+{code:X4}: {PythonCharacterData.Lower(code):X4} != {want:X4}");
            }
        }

        mismatches.Should().BeEmpty();
    }

    [Fact]
    public void IsAlnumMatchesStrIsAlnumOnEveryCodePoint()
    {
        var alnum = RangesOf(Data.Value["alnum"]);
        var mismatches = new List<string>();
        for (var code = 0; code <= CodePointSet.MaxCodePoint; code++)
        {
            if (code is >= 0xD800 and <= 0xDFFF)
            {
                continue;
            }

            var rune = new System.Text.Rune(code);
            if (PythonText.IsAlnum(rune) != alnum.Contains(code) || PythonText.IsWordRune(rune) != (code == '_' || alnum.Contains(code)))
            {
                mismatches.Add($"U+{code:X4}");
            }
        }

        mismatches.Should().BeEmpty();
    }

    [Fact]
    public void IsPrintableMatchesStrIsPrintableOnEveryCodePoint()
    {
        var nonPrintable = RangesOf(Data.Value["nonprintable"]);
        var mismatches = new List<string>();
        for (var code = 0; code <= CodePointSet.MaxCodePoint; code++)
        {
            if (PythonText.IsPrintable(code) == nonPrintable.Contains(code))
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
        var actual = CodePointSet.FromPredicate(PythonCharacterData.IsCased);
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
        var parsed = ReParser.Parse(pattern, PythonReFlags.None);
        parsed.Nodes.Should().ContainSingle();
        var actual = ReCharacterSets.NodeSet(parsed.Nodes[0], parsed.State.Flags);

        expected.Except(actual).Ranges.Should().BeEmpty($"Python matches these for {pattern}");
        actual.Except(expected).Ranges.Should().BeEmpty($"only Python matches these for {pattern}");

        var compiled = PythonPattern.Compile(pattern);
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
        var flags = (PythonReFlags)(int)entry["flags"]!;
        if (entry.TryGetValue("error", out var error))
        {
            AssertCompileError(pattern, flags, (OrderedDictionary<string, object?>)error!);
            return;
        }

        var compiled = PythonPattern.Compile(pattern, flags);
        compiled.Groups.Should().Be((int)entry["groups"]!);
        foreach (var run in ((List<object?>)entry["runs"]!).Cast<OrderedDictionary<string, object?>>())
        {
            var text = (string)run["text"]!;
            var expectedIter = ((List<object?>)run["finditer"]!).Select(Render).ToList();
            compiled.FindIter(text, TestContext.Current.CancellationToken).Select(match => Render(compiled, text, match)).Should().Equal(expectedIter, $"finditer of {pattern} over {PythonRepr.StrRepr(text)}");
            var search = compiled.Search(text, TestContext.Current.CancellationToken);
            (search is null ? "None" : Render(compiled, text, search)).Should().Be(run["search"] is null ? "None" : Render(run["search"]));
        }
    }

    private static void AssertCompileError(string pattern, PythonReFlags flags, OrderedDictionary<string, object?> error)
    {
        var compile = () => PythonPattern.Compile(pattern, flags);
        switch ((string)error["type"]!)
        {
            case "OverflowError":
                compile.Should().Throw<OverflowException>().WithMessage((string)error["message"]!);
                break;
            default:
                var thrown = compile.Should().Throw<PythonReException>().Which;
                thrown.Message.Should().Be((string)error["message"]!);
                thrown.Position.Should().Be(error["pos"] is null ? null : (int)error["pos"]!);
                break;
        }
    }

    private static string Render(object? record)
    {
        var map = (OrderedDictionary<string, object?>)record!;
        var span = (List<object?>)map["span"]!;
        var groups = ((List<object?>)map["groups"]!).Select(group => group is null ? "None" : PythonRepr.StrRepr((string)group));
        return $"({span[0]}, {span[1]}) [{string.Join(", ", groups)}] last={map["lastindex"]?.ToString() ?? "None"}";
    }

    private static string Render(PythonPattern pattern, string text, PythonMatch match)
    {
        var groups = Enumerable.Range(0, pattern.Groups + 1).Select(index => match.Group(index) is { } value ? PythonRepr.StrRepr(value) : "None");
        return $"({CodePointOffset(text, match.Start)}, {CodePointOffset(text, match.End)}) [{string.Join(", ", groups)}] last={match.LastIndex?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "None"}";
    }

    private static int CodePointOffset(string text, int utf16Offset) => ReTokenizer.CodePoints(text[..utf16Offset]).Length;

    [Fact]
    public void OracleCoversTheShippedPatterns()
    {
        var patterns = ((List<object?>)Data.Value["patterns"]!).Cast<OrderedDictionary<string, object?>>().Select(entry => (string)entry["pattern"]!).ToList();
        patterns.Should().Contain("AKIA[0-9A-Z]{16}");
        patterns.Should().HaveCountGreaterThan(90);
    }
}
