using System.Text;

using DriftBuster.Backend.Detection;
using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Profiles.Detection;

namespace DriftBuster.Backend.Tests.Profiles.Detection;

/// <summary>
/// CPython 3.13 oracle cases for core/profiles.py beyond tests/core/test_profiles.py: <c>from_dict</c> over malformed and
/// unusual JSON, <c>diff_summary_snapshots</c> over summaries Python reads leniently, dict ordering after updates, error texts,
/// dataclass equality and a profile-aware scan through a nested directory. Expected values are the interpreter's repr of the
/// payload (to_dict() or the diff) or the exception it raises.
/// </summary>
public sealed class DetectionProfileStoreEdgeTests : IDisposable
{
    private readonly DirectoryInfo _tmp = Directory.CreateTempSubdirectory("driftbuster-profile-edges-");

    public void Dispose() => _tmp.Delete(recursive: true);

    private static object? Json(string text)
    {
        PythonJson.TryLoads(text, out var value).Should().BeTrue();
        return value;
    }

    internal static void AssertOutcome(string caseName, Func<string> act, string kind, string expected)
    {
        if (string.Equals(kind, "ok", StringComparison.Ordinal))
        {
            act().Should().Be(expected, caseName);
            return;
        }

        var thrown = act.Should().Throw<Exception>().Which;
        thrown.GetType().Name.Should().Be(kind, caseName);
        if (expected.Length > 0)
        {
            thrown.Message.Should().Be(expected, caseName);
        }
    }

    [Theory]
    [InlineData("payload-list", "[]", "PythonAttributeException", "'list' object has no attribute 'get'")]
    [InlineData("profiles-null", "{\"profiles\": null}", "PythonTypeException", "'NoneType' object is not iterable")]
    [InlineData("profiles-str", "{\"profiles\": \"ab\"}", "PythonAttributeException", "'str' object has no attribute 'get'")]
    [InlineData("entry-no-name", "{\"profiles\": [{\"configs\": []}]}", "KeyNotFoundException", "'name'")]
    [InlineData("config-str", "{\"profiles\": [{\"name\": \"p\", \"configs\": [\"x\"]}]}", "PythonTypeException", "string indices must be integers, not 'str'")]
    [InlineData("config-int", "{\"profiles\": [{\"name\": \"p\", \"configs\": [5]}]}", "PythonTypeException", "'int' object is not subscriptable")]
    [InlineData("config-list", "{\"profiles\": [{\"name\": \"p\", \"configs\": [[1]]}]}", "PythonTypeException", "list indices must be integers or slices, not str")]
    [InlineData("config-no-id", "{\"profiles\": [{\"name\": \"p\", \"configs\": [{}]}]}", "KeyNotFoundException", "'id'")]
    [InlineData("path-int", "{\"profiles\": [{\"name\": \"p\", \"configs\": [{\"id\": \"c\", \"path\": 5}]}]}", "PythonTypeException", "argument should be a str or an os.PathLike object where __fspath__ returns a str, not 'int'")]
    [InlineData("tags-int-item", "{\"profiles\": [{\"name\": \"p\", \"tags\": [\"a\", 0, null, 3]}]}", "PythonAttributeException", "'int' object has no attribute 'strip'")]
    [InlineData("tags-int", "{\"profiles\": [{\"name\": \"p\", \"tags\": 7}]}", "PythonTypeException", "'int' object is not iterable")]
    [InlineData("tags-str", "{\"profiles\": [{\"name\": \"p\", \"tags\": \" ab\"}]}", "ok", "{'profiles': [{'name': 'p', 'description': None, 'tags': ['a', 'b'], 'metadata': {}, 'configs': []}]}")]
    [InlineData("metadata-str", "{\"profiles\": [{\"name\": \"p\", \"metadata\": \"ab\"}]}", "PythonValueException", "dictionary update sequence element #0 has length 1; 2 is required")]
    [InlineData("metadata-int", "{\"profiles\": [{\"name\": \"p\", \"metadata\": 3}]}", "PythonTypeException", "'int' object is not iterable")]
    [InlineData("metadata-pairs", "{\"profiles\": [{\"name\": \"p\", \"metadata\": [[\"k\", 1], \"xy\", {\"m\": 1, \"n\": 2}]}]}", "ok", "{'profiles': [{'name': 'p', 'description': None, 'tags': [], 'metadata': {'k': 1, 'x': 'y', 'm': 'n'}, 'configs': []}]}")]
    [InlineData("metadata-bad-length", "{\"profiles\": [{\"name\": \"p\", \"metadata\": [[\"k\"]]}]}", "PythonValueException", "dictionary update sequence element #0 has length 1; 2 is required")]
    [InlineData("metadata-bad-element", "{\"profiles\": [{\"name\": \"p\", \"metadata\": [[\"k\", 1], 5]}]}", "PythonTypeException", "cannot convert dictionary update sequence element #1 to a sequence")]
    [InlineData("metadata-unhashable-key", "{\"profiles\": [{\"name\": \"p\", \"metadata\": [[[1], 2]]}]}", "PythonTypeException", "unhashable type: 'list'")]
    [InlineData("name-list", "{\"profiles\": [{\"name\": [\"x\"]}]}", "PythonTypeException", "unhashable type: 'list'")]
    [InlineData("id-dict", "{\"profiles\": [{\"name\": \"p\", \"configs\": [{\"id\": {\"a\": 1}}]}]}", "PythonTypeException", "unhashable type: 'dict'")]
    [InlineData("dup-names", "{\"profiles\": [{\"name\": \"p\"}, {\"name\": \"p\"}]}", "PythonValueException", "Profile 'p' is already registered")]
    [InlineData("dup-before-unhashable", "{\"profiles\": [{\"name\": \"p\", \"configs\": [{\"id\": \"a\"}, {\"id\": \"a\"}, {\"id\": [\"z\"]}]}]}", "PythonValueException", "Duplicate config identifier 'a' within profile 'p'")]
    [InlineData("dup-cross", "{\"profiles\": [{\"name\": \"p\", \"configs\": [{\"id\": \"a\"}]}, {\"name\": \"q\", \"configs\": [{\"id\": \"a\"}]}]}", "PythonValueException", "Config identifier 'a' already registered under profile 'p'")]
    [InlineData("strings", "{\"profiles\": [{\"name\": \"p\", \"description\": \"d\", \"tags\": [\"b\", \"a\", \"\\ud83d\\ude00\", \"\\uffff\"], \"metadata\": {\"z\": 1, \"a\": [1]}, \"configs\": [{\"id\": \"c\", \"path\": \"./x//y/\", \"path_glob\": \"a/*/\", \"application\": \"app\", \"version\": \"1.2\", \"branch\": \"main\", \"tags\": [\" t \"], \"expected_format\": \"json\", \"expected_variant\": \"v\", \"metadata\": {\"ignore_review_flags\": true}}]}]}", "ok", "{'profiles': [{'name': 'p', 'description': 'd', 'tags': ['a', 'b', '\\uffff', '😀'], 'metadata': {'z': 1, 'a': [1]}, 'configs': [{'id': 'c', 'path': 'x/y', 'path_glob': 'a/*', 'application': 'app', 'version': '1.2', 'branch': 'main', 'tags': ['t'], 'expected_format': 'json', 'expected_variant': 'v', 'metadata': {'ignore_review_flags': True}}]}]}")]
    public void FromDictMatchesPython(string caseName, string payload, string kind, string expected)
        => AssertOutcome(caseName, () => PythonRepr.Repr(DetectionProfileStore.FromDict(Json(payload)).ToDict()), kind, expected);

    [Theory]
    [InlineData("strings-counts", "{\"total_profiles\": \"3\", \"total_configs\": 2.7, \"profiles\": [{\"name\": \"a\", \"config_ids\": [\"x\"], \"config_count\": \"5\"}]}", "{\"total_profiles\": null, \"total_configs\": [1], \"profiles\": [{\"name\": \"a\", \"config_ids\": [\"x\", \"y\"]}, {\"name\": \"b\"}]}", "ok", "{'totals': {'baseline': {'profiles': 3, 'configs': 2}, 'current': {'profiles': 2, 'configs': 2}}, 'added_profiles': ['b'], 'removed_profiles': [], 'changed_profiles': [{'name': 'a', 'baseline_config_count': 5, 'current_config_count': 2, 'added_config_ids': ['y'], 'removed_config_ids': []}]}")]
    [InlineData("zero-totals", "{\"total_profiles\": 0, \"total_configs\": \"0\", \"profiles\": [{\"name\": \"a\", \"config_ids\": [\"x\", \"x\"]}]}", "{\"profiles\": []}", "ok", "{'totals': {'baseline': {'profiles': 1, 'configs': 2}, 'current': {'profiles': 0, 'configs': 0}}, 'added_profiles': [], 'removed_profiles': ['a'], 'changed_profiles': []}")]
    [InlineData("count-only-change", "{\"profiles\": [{\"name\": \"a\", \"config_ids\": [\"x\"], \"config_count\": 1}]}", "{\"profiles\": [{\"name\": \"a\", \"config_ids\": [\"x\"], \"config_count\": 2}]}", "ok", "{'totals': {'baseline': {'profiles': 1, 'configs': 1}, 'current': {'profiles': 1, 'configs': 2}}, 'added_profiles': [], 'removed_profiles': [], 'changed_profiles': [{'name': 'a', 'baseline_config_count': 1, 'current_config_count': 2, 'added_config_ids': [], 'removed_config_ids': []}]}")]
    [InlineData("duplicate-names", "{\"profiles\": [{\"name\": \"a\", \"config_ids\": [\"x\"]}, {\"name\": \"a\", \"config_ids\": [\"y\"]}]}", "{\"profiles\": [{\"name\": \"a\", \"config_ids\": [\"x\"]}]}", "ok", "{'totals': {'baseline': {'profiles': 1, 'configs': 2}, 'current': {'profiles': 1, 'configs': 1}}, 'added_profiles': [], 'removed_profiles': [], 'changed_profiles': [{'name': 'a', 'baseline_config_count': 1, 'current_config_count': 1, 'added_config_ids': ['x'], 'removed_config_ids': ['y']}]}")]
    [InlineData("numeric-names", "{\"profiles\": [{\"name\": 1, \"config_ids\": [1, 2.0]}, {\"name\": \"b\"}]}", "{\"profiles\": [{\"name\": true, \"config_ids\": [true, 3]}, {\"name\": 1.0}]}", "ok", "{'totals': {'baseline': {'profiles': 2, 'configs': 2}, 'current': {'profiles': 1, 'configs': 2}}, 'added_profiles': [], 'removed_profiles': ['b'], 'changed_profiles': [{'name': True, 'baseline_config_count': 2, 'current_config_count': 0, 'added_config_ids': [], 'removed_config_ids': [1, 2.0]}]}")]
    [InlineData("intersection-representative", "{\"profiles\": [{\"name\": 1, \"config_ids\": []}, {\"name\": 2}]}", "{\"profiles\": [{\"name\": 1.0, \"config_count\": 4}]}", "ok", "{'totals': {'baseline': {'profiles': 2, 'configs': 0}, 'current': {'profiles': 1, 'configs': 4}}, 'added_profiles': [], 'removed_profiles': [2], 'changed_profiles': [{'name': 1.0, 'baseline_config_count': 0, 'current_config_count': 4, 'added_config_ids': [], 'removed_config_ids': []}]}")]
    [InlineData("big", "{\"total_profiles\": 1000000000000000000000000000000, \"profiles\": [{\"name\": \"a\", \"config_count\": \"100000000000000000000\"}]}", "{\"profiles\": [{\"name\": \"a\", \"config_count\": -3}]}", "ok", "{'totals': {'baseline': {'profiles': 1000000000000000000000000000000, 'configs': 100000000000000000000}, 'current': {'profiles': 1, 'configs': -3}}, 'added_profiles': [], 'removed_profiles': [], 'changed_profiles': [{'name': 'a', 'baseline_config_count': 100000000000000000000, 'current_config_count': -3, 'added_config_ids': [], 'removed_config_ids': []}]}")]
    [InlineData("sorted-codepoints", "{\"profiles\": []}", "{\"profiles\": [{\"name\": \"\\uffff\"}, {\"name\": \"\\ud83d\\ude00\"}, {\"name\": \"B\"}, {\"name\": \"a\"}]}", "ok", "{'totals': {'baseline': {'profiles': 0, 'configs': 0}, 'current': {'profiles': 4, 'configs': 0}}, 'added_profiles': ['B', 'a', '\\uffff', '😀'], 'removed_profiles': [], 'changed_profiles': []}")]
    [InlineData("unhashable-name", "{\"profiles\": [{\"name\": [\"x\"]}]}", "{}", "PythonTypeException", "unhashable type: 'list'")]
    [InlineData("unhashable-id", "{\"profiles\": [{\"name\": \"a\", \"config_ids\": [[\"x\"]]}]}", "{}", "PythonTypeException", "unhashable type: 'list'")]
    [InlineData("entry-str", "{\"profiles\": [\"x\"]}", "{}", "PythonAttributeException", "'str' object has no attribute 'get'")]
    [InlineData("profiles-null", "{\"profiles\": null}", "{}", "PythonTypeException", "'NoneType' object is not iterable")]
    [InlineData("config-ids-null", "{\"profiles\": [{\"name\": \"a\", \"config_ids\": null}]}", "{}", "PythonTypeException", "'NoneType' object is not iterable")]
    [InlineData("config-ids-str", "{\"profiles\": [{\"name\": \"a\", \"config_ids\": \"xy\"}]}", "{\"profiles\": [{\"name\": \"a\", \"config_ids\": \"yz\"}]}", "ok", "{'totals': {'baseline': {'profiles': 1, 'configs': 2}, 'current': {'profiles': 1, 'configs': 2}}, 'added_profiles': [], 'removed_profiles': [], 'changed_profiles': [{'name': 'a', 'baseline_config_count': 2, 'current_config_count': 2, 'added_config_ids': ['z'], 'removed_config_ids': ['x']}]}")]
    [InlineData("summary-list", "[]", "{}", "PythonAttributeException", "'list' object has no attribute 'get'")]
    [InlineData("mixed-sort", "{}", "{\"profiles\": [{\"name\": \"a\"}, {\"name\": 1}]}", "PythonTypeException", "")]
    public void DiffSummarySnapshotsMatchesPython(string caseName, string baseline, string current, string kind, string expected)
        => AssertOutcome(caseName, () => PythonRepr.Repr(DetectionProfileStore.DiffSummarySnapshots(Json(baseline), Json(current))), kind, expected);

    [Fact]
    public void DiffSummarySnapshotsReadsNonFiniteCountsAsIntDoes()
    {
        var infinite = () => DetectionProfileStore.DiffSummarySnapshots(
            new OrderedDictionary<string, object?>(StringComparer.Ordinal) { ["total_profiles"] = double.PositiveInfinity },
            new OrderedDictionary<string, object?>(StringComparer.Ordinal));
        infinite.Should().Throw<OverflowException>().WithMessage("cannot convert float infinity to integer");

        var nan = DetectionProfileStore.DiffSummarySnapshots(
            new OrderedDictionary<string, object?>(StringComparer.Ordinal) { ["total_profiles"] = double.NaN },
            new OrderedDictionary<string, object?>(StringComparer.Ordinal));
        PythonRepr.Repr(nan).Should().Be(
            "{'totals': {'baseline': {'profiles': 0, 'configs': 0}, 'current': {'profiles': 0, 'configs': 0}}, 'added_profiles': [], "
            + "'removed_profiles': [], 'changed_profiles': []}");
    }

    [Fact]
    public void UpdatesMoveProfilesToTheEndAndErrorsCarryPythonsText()
    {
        var store = new DetectionProfileStore(
        [
            new DetectionProfile("a", configs: [new DetectionProfileConfig("1")]),
            new DetectionProfile("b"),
            new DetectionProfile("c"),
        ]);

        store.UpdateProfile("a", profile => profile);
        store.Profiles().Select(profile => profile.Name).Should().Equal("b", "c", "a");

        var clash = () => store.UpdateProfile("b", _ => new DetectionProfile("b", configs: [new DetectionProfileConfig("1")]));
        clash.Should().Throw<PythonValueException>().WithMessage("Config identifier '1' already registered under profile 'a'");
        store.Profiles().Select(profile => profile.Name).Should().Equal("c", "a", "b");
        store.FindConfig("1").Should().ContainSingle().Which.Profile.Name.Should().Be("a");

        var removeFromMissing = () => store.RemoveConfig("zz", "1");
        removeFromMissing.Should().Throw<KeyNotFoundException>().WithMessage("\"Profile 'zz' is not registered\"");
        var getMissing = () => store.GetProfile("zz");
        getMissing.Should().Throw<KeyNotFoundException>().WithMessage("'zz'");
        var removeMissing = () => store.RemoveProfile("zz");
        removeMissing.Should().Throw<KeyNotFoundException>().WithMessage("'zz'");
        var updateMissing = () => store.UpdateProfile("zz", profile => profile);
        updateMissing.Should().Throw<KeyNotFoundException>().WithMessage("\"Profile 'zz' is not registered\"");
        var removeMissingConfig = () => store.RemoveConfig("a", "zz");
        removeMissingConfig.Should().Throw<PythonValueException>().WithMessage("Config identifier 'zz' is not registered under profile 'a'");
        var renameOnto = () => store.UpdateProfile("a", profile => profile with { Name = "c" });
        renameOnto.Should().Throw<PythonValueException>().WithMessage("Profile 'c' is already registered");

        PythonRepr.Repr(store.Summary()).Should().Be(
            "{'total_profiles': 3, 'total_configs': 1, 'profiles': [{'name': 'a', 'description': None, 'tags': [], 'config_count': 1, "
            + "'config_ids': ['1']}, {'name': 'b', 'description': None, 'tags': [], 'config_count': 0, 'config_ids': []}, {'name': 'c', "
            + "'description': None, 'tags': [], 'config_count': 0, 'config_ids': []}]}");
    }

    [Fact]
    public void RenamedProfileIsReindexedUnderItsNewName()
    {
        var store = new DetectionProfileStore([new DetectionProfile("a", configs: [new DetectionProfileConfig("1")])]);

        var renamed = store.UpdateProfile("a", profile => profile with { Name = "z", Description = "moved" });

        store.Profiles().Should().Equal(renamed);
        store.FindConfig("1").Should().ContainSingle().Which.Profile.Should().BeSameAs(renamed);
        var getOld = () => store.GetProfile("a");
        getOld.Should().Throw<KeyNotFoundException>();
    }

    [Fact]
    public void ProfilesCompareByValueAsDataclassesDo()
    {
        var metadata = new Dictionary<string, object?>(StringComparer.Ordinal) { ["n"] = 1L, ["list"] = new List<object?> { 1, "x" } };
        var left = new DetectionProfileConfig("c", path: "a//b", tags: [" t", "t"], metadata: metadata);
        var right = new DetectionProfileConfig(
            "c",
            path: "a/b",
            tags: ["t"],
            metadata: new Dictionary<string, object?>(StringComparer.Ordinal) { ["list"] = new List<object?> { 1.0, "x" }, ["n"] = true });

        left.Should().Be(right);
        left.GetHashCode().Should().Be(right.GetHashCode());
        (left with { Branch = "main" }).Should().NotBe(right);
        new DetectionProfile("p", tags: ["x"], configs: [left]).Should().Be(new DetectionProfile("p", tags: [" x "], configs: [right]));
        new DetectionProfile("p", configs: [left]).Should().NotBe(new DetectionProfile("p", configs: [left, right]));

        (left with { PathGlob = "./g//*" }).PathGlob.Should().Be("g/*");
        (left with { Tags = new HashSet<string>(["  "], StringComparer.Ordinal) }).Tags.Should().BeEmpty();
        metadata["n"] = 2L;
        left.Metadata["n"].Should().Be(1L);
    }

    [Fact]
    public void MatchingHonoursVersionAndBranchTagsAndGlobsAcrossSeparators()
    {
        var config = new DetectionProfileConfig("c", pathGlob: "configs/*.json", version: "2", branch: "main");
        var tags = new HashSet<string>(["version:2", "branch:main"], StringComparer.Ordinal);

        config.Matches("configs/sub/app.json", tags).Should().BeTrue();
        config.Matches("./configs//app.json", tags).Should().BeTrue();
        config.Matches("configs/app.json", new HashSet<string>(["version:2"], StringComparer.Ordinal)).Should().BeFalse();
        config.Matches(null, tags).Should().BeFalse();

        var exact = new DetectionProfileConfig("e", path: "configs/app.json", pathGlob: "*.yaml");
        exact.Matches("configs/app.json", tags).Should().BeTrue();
        exact.Matches("x.yaml", tags).Should().BeTrue();
        exact.Matches("x.json", tags).Should().BeFalse();
    }

    [Fact]
    public void ScanWithProfilesMatchesNestedPathsRelativeToTheRoot()
    {
        var nested = Directory.CreateDirectory(Path.Combine(_tmp.FullName, "configs", "sub")).FullName;
        File.WriteAllText(Path.Combine(nested, "app.json"), "{\"a\": 1}", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(_tmp.FullName, "top.json"), "{\"b\": 2}", new UTF8Encoding(false));
        var store = new DetectionProfileStore(
        [
            new DetectionProfile(
                "nested",
                configs:
                [
                    new DetectionProfileConfig("glob", pathGlob: "configs/*.json"),
                    new DetectionProfileConfig("top", path: "top.json", application: "svc"),
                ]),
        ]);

        var results = new Detector().ScanWithProfiles(_tmp.FullName + "/", store, tags: ["application:svc"]);

        results.Select(result => (PathText.Name(result.Path), string.Join(",", result.Profiles.Select(applied => applied.Config.Identifier))))
            .Should().Equal(("app.json", "glob"), ("top.json", "top"));
        results.Should().OnlyContain(result => result.Detection != null && !result.Detection.Metadata!.ContainsKey("review_ignored"));
    }
}
