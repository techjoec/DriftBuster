using DriftBuster.Backend.Hunt;
using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Tests.Hunt;

/// <summary>
/// <see cref="HuntEngine"/> against CPython's <c>hunt_path(..., return_json=True)</c> on <c>Data/hunt_cases.json</c>
/// (written by <c>tools/parity/gen_hunt_secret_cases.py</c> with fix e applied to the Python rules): the same tree is
/// rebuilt byte for byte and every run's entries (walk order, excludes, globs, sample size, file roots, capture groups,
/// <c>lastindex</c>, keyword gates, plan transforms) must render identically.
/// </summary>
[Collection(HuntSeamCollection.Name)]
public sealed class HuntOracleTests : IDisposable
{
    private static readonly Lazy<OrderedDictionary<string, object?>> Data = new(() =>
    {
        var path = Path.Combine(RepoPaths.Root, "gui", "DriftBuster.Backend.Tests", "Hunt", "Data", "hunt_cases.json");
        PythonJson.TryLoads(File.ReadAllText(path), out var value).Should().BeTrue();
        return (OrderedDictionary<string, object?>)((List<object?>)value!)[0]!;
    });

    private readonly DirectoryInfo _tmp = Directory.CreateTempSubdirectory("driftbuster-hunt-oracle-");

    public HuntOracleTests()
    {
        foreach (var (relative, content) in (OrderedDictionary<string, object?>)Data.Value["files"]!)
        {
            var target = Path.Combine([Root, .. relative.Split('/')]);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, Convert.FromBase64String((string)content!));
        }
    }

    private string Root => Path.Combine(_tmp.FullName, "tree");

    public static TheoryData<string> RunNames
    {
        get
        {
            var names = new TheoryData<string>();
            foreach (var run in (List<object?>)Data.Value["runs"]!)
            {
                names.Add((string)((OrderedDictionary<string, object?>)run!)["name"]!);
            }

            return names;
        }
    }

    public void Dispose() => _tmp.Delete(recursive: true);

    private static IReadOnlyList<HuntRule> CustomRules() =>
    [
        new HuntRule("groups", "nested groups", "  grouped  ", patterns: [@"((host)[:=]\s*(\S+))"]),
        new HuntRule("blank-token", "blank token", "   ", ["SERVER"], ["server"]),
        new HuntRule("no-patterns", "keywords only", "line", ["İ"]),
        new HuntRule("empty-match", "zero width", "empty", ["version"], ["x*"]),
        new HuntRule("lookahead", "lookahead groups", "ahead", patterns: [@"(?=(\w+)\.local)(\w)"]),
        new HuntRule("engine", "sre repeat semantics", "engine", patterns: [@"(?:\d{1,2}){2}+", @"\N{LATIN SMALL LETTER E}(?:r?)+?v", @"(?i)(s)\1"]),
    ];

    [Theory]
    [MemberData(nameof(RunNames))]
    public void HuntJsonMatchesPython(string name)
    {
        var run = ((List<object?>)Data.Value["runs"]!).Cast<OrderedDictionary<string, object?>>()
            .Single(entry => string.Equals((string)entry["name"]!, name, StringComparison.Ordinal));
        var rootName = (string)run["root"]!;
        var root = rootName.Length == 0 ? Root : Path.Combine(Root, rootName);
        var rules = string.Equals((string)run["rules"]!, "default", StringComparison.Ordinal) ? HuntRules.Default : CustomRules();
        var exclude = run["exclude"] is List<object?> patterns ? patterns.Cast<string>().ToList() : null;

        var result = HuntEngine.HuntPath(root, rules, (string)run["glob"]!, (int)run["sample_size"]!, exclude, TestContext.Current.CancellationToken);
        var actual = HuntEngine.ToJson(result).Select(entry =>
        {
            entry["path"] = "<root>" + ((string)entry["path"]!)[Root.Length..];
            return PythonRepr.Repr(entry);
        });

        result.UnreadableFiles.Should().BeEmpty();
        actual.Should().Equal(((List<object?>)run["result"]!).Select(PythonRepr.Repr));
    }
}
