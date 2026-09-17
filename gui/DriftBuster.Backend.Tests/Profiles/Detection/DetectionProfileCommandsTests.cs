using System.Text;
using System.Text.Json;

using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Profiles.Detection;
using DriftBuster.Backend.Tests.Infrastructure;

namespace DriftBuster.Backend.Tests.Profiles.Detection;

/// <summary>
/// Mirror of the library half of tests/cli/test_profile_cli.py, plus CPython 3.13 oracle cases for <c>_load_json</c>,
/// <c>_store_from_payload</c> and the hunt bridge. Python drives <c>profile_cli.main(argv)</c> and reads stdout, the output file
/// or the exit code; here the same inputs go through <see cref="DetectionProfileCommands"/> and the payload assertions are
/// Python's. Exit codes, stdout, <c>--output</c>, <c>--indent</c>, <c>--sort-keys</c> and <c>test_parse_args_requires_command</c>
/// belong to the console tool. Every test that builds a store from a payload lives in this class, so the
/// <see cref="DetectionProfileCommands.FromDict"/> seam one test swaps is never seen by another running concurrently.
/// </summary>
public sealed class DetectionProfileCommandsTests : IDisposable
{
    private readonly DirectoryInfo _tmp = Directory.CreateTempSubdirectory("driftbuster-profile-cli-");

    public void Dispose() => _tmp.Delete(recursive: true);

    private string Write(string name, string json)
    {
        var path = Path.Combine(_tmp.FullName, name);
        File.WriteAllText(path, json, new UTF8Encoding(false));
        return path;
    }

    private static OrderedDictionary<string, object?> Map(object? value) => value.Should().BeOfType<OrderedDictionary<string, object?>>().Subject;

    private static List<object?> Items(object? value) => value.Should().BeOfType<List<object?>>().Subject;

    private static object? Json(string text)
    {
        PythonJson.TryLoads(text, out var value).Should().BeTrue();
        return value;
    }

    [Fact]
    public void ProfileCliSummary()
    {
        var storePath = Write("store.json", """{"profiles": [{"name": "prod", "configs": [{"id": "cfg1", "path": "configs/app.config"}]}]}""");

        var data = DetectionProfileCommands.Summary(storePath);

        data["total_profiles"].Should().Be(1);
        data["total_configs"].Should().Be(1);
    }

    [Fact]
    public void ProfileCliDiff()
    {
        var baseline = Write("baseline.json", """{"total_profiles": 1, "total_configs": 1, "profiles": []}""");
        var current = Write("current.json", """{"total_profiles": 2, "total_configs": 3, "profiles": []}""");

        var diff = DetectionProfileCommands.Diff(baseline, current);

        Map(Map(diff["totals"])["current"])["profiles"].Should().Be(2);
    }

    [Fact]
    public void ProfileCliHuntBridge()
    {
        var storePath = Write(
            "store.json",
            """
            {"profiles": [{"name": "prod", "tags": ["prod"], "configs": [{"id": "cfg1", "path": "configs/appsettings.json",
              "expected_format": "json", "expected_variant": "structured-settings-json"}]}]}
            """);
        var huntPath = Write(
            "hunts.json",
            """
            [{"rule": {"name": "server-name", "description": "", "token_name": "server"}, "relative_path": "configs/appsettings.json",
              "path": "dummy", "line_number": 1, "excerpt": "server: dev"}]
            """);

        var payload = DetectionProfileCommands.HuntBridge(storePath, huntPath, ["prod"], root: null);

        Map(Items(Map(Items(payload["items"])[0])["profiles"])[0])["profile"].Should().Be("prod");
    }

    [Fact]
    public void ProfileCliSummaryWritesOutputFile()
    {
        var storePath = Write("store.json", """{"profiles": [{"name": "prod", "configs": [{"id": "cfg1"}]}]}""");

        var data = DetectionProfileCommands.Summary(storePath);

        data["total_profiles"].Should().Be(1);
    }

    [Fact]
    public void StoreFromPayloadIgnoresInvalidEntries()
    {
        var original = DetectionProfileCommands.FromDict;
        DetectionProfileCommands.FromDict = _ => throw new InvalidOperationException("fallback");
        try
        {
            var payload = Json("""{"profiles": ["invalid", {"name": "demo", "configs": ["skip", {"id": "cfg", "path": "config.json"}]}]}""");
            var store = DetectionProfileCommands.StoreFromPayload(payload);
            var summary = store.Summary();
            summary["total_profiles"].Should().Be(1);
        }
        finally
        {
            DetectionProfileCommands.FromDict = original;
        }
    }

    [Fact]
    public void HandleHuntBridgeValidatesPayload()
    {
        var storePath = Write("store.json", """{"profiles": []}""");

        var invalid = () => DetectionProfileCommands.HuntBridge(storePath, storePath, tags: null, root: _tmp.FullName);
        invalid.Should().Throw<PythonValueException>();

        var outside = Path.Combine(_tmp.Parent!.FullName, "outside.txt");
        var huntPath = Write(
            "hunts.json",
            $$"""
            [{"rule": {"name": "server", "description": ""}, "path": {{JsonSerializer.Serialize(outside)}}, "line_number": 1, "excerpt": "server"},
             {"rule": {"name": "missing", "description": ""}, "line_number": 2, "excerpt": "data"}]
            """);
        Write("store.json", """{"profiles": []}""");

        var result = DetectionProfileCommands.HuntBridge(storePath, huntPath, tags: null, root: _tmp.FullName);

        // Python asserts only the exit code; the items it would write are these.
        Items(result["items"]).Select(item => Map(item)["relative_path"]).Should().Equal(new object?[] { "outside.txt", null });
    }

    // _store_from_payload over each payload: from_dict first, the lenient build when it raises. Expected values are CPython 3.13's
    // repr of the normalised to_dict() payload, or the exception it raises.
    [Theory]
    [InlineData("payload-list", "[]", "PythonAttributeException", "'list' object has no attribute 'get'")]
    [InlineData("profiles-str", "{\"profiles\": \"ab\"}", "ok", "{'profiles': []}")]
    [InlineData("config-str", "{\"profiles\": [{\"name\": \"p\", \"configs\": [\"x\"]}]}", "ok", "{'profiles': [{'name': 'p', 'description': None, 'tags': [], 'metadata': {}, 'configs': []}]}")]
    [InlineData("config-int", "{\"profiles\": [{\"name\": \"p\", \"configs\": [5]}]}", "ok", "{'profiles': [{'name': 'p', 'description': None, 'tags': [], 'metadata': {}, 'configs': []}]}")]
    [InlineData("config-no-id", "{\"profiles\": [{\"name\": \"p\", \"configs\": [{}]}]}", "KeyNotFoundException", "'id'")]
    [InlineData("name-list", "{\"profiles\": [{\"name\": [\"x\"]}]}", "ok", "{'profiles': [{'name': \"['x']\", 'description': None, 'tags': [], 'metadata': {}, 'configs': []}]}")]
    [InlineData("id-dict", "{\"profiles\": [{\"name\": \"p\", \"configs\": [{\"id\": {\"a\": 1}}]}]}", "ok", "{'profiles': [{'name': 'p', 'description': None, 'tags': [], 'metadata': {}, 'configs': [{'id': \"{'a': 1}\", 'path': None, 'path_glob': None, 'application': None, 'version': None, 'branch': None, 'tags': [], 'expected_format': None, 'expected_variant': None, 'metadata': {}}]}]}")]
    [InlineData("entry-no-name", "{\"profiles\": [{\"configs\": []}]}", "KeyNotFoundException", "'name'")]
    [InlineData("path-int", "{\"profiles\": [{\"name\": \"p\", \"configs\": [{\"id\": \"c\", \"path\": 5}]}]}", "PythonTypeException", "argument should be a str or an os.PathLike object where __fspath__ returns a str, not 'int'")]
    [InlineData("dup-names", "{\"profiles\": [{\"name\": \"p\"}, {\"name\": \"p\"}]}", "PythonValueException", "Profile 'p' is already registered")]
    public void StoreFromPayloadMatchesPython(string caseName, string payload, string kind, string expected)
    {
        var act = () => PythonRepr.Repr(DetectionProfileCommands.StoreFromPayload(Json(payload)).ToDict());
        DetectionProfileStoreEdgeTests.AssertOutcome(caseName, act, kind, expected);
    }

    [Fact]
    public void StoreFromPayloadWithoutFromDictUsesTheLenientBuild()
    {
        var original = DetectionProfileCommands.FromDict;
        DetectionProfileCommands.FromDict = null;
        try
        {
            var store = DetectionProfileCommands.StoreFromPayload(Json("""{"profiles": [{"name": 5, "configs": [{"id": 1.5}]}]}"""));
            store.FindConfig("1.5").Should().ContainSingle().Which.Profile.Name.Should().Be("5");
        }
        finally
        {
            DetectionProfileCommands.FromDict = original;
        }
    }

    // Recorded divergence: Python stores a non-str application or version as the JSON value; the typed port stores its str() text,
    // which is what Python matches against (f"application:{value}"), so matching agrees and to_dict carries the text.
    [Fact]
    public void StoreFromPayloadStoresNonStringTextFieldsAsTheirStrText()
    {
        var store = DetectionProfileCommands.StoreFromPayload(
            Json("""{"profiles": ["skip", {"name": 5, "configs": ["skip", {"id": 1.5, "application": 2, "version": true}]}]}"""));

        store.MatchingConfigs(["application:2", "version:True"], relativePath: null)
            .Select(match => (match.Profile.Name, match.Config.Identifier)).Should().Equal(("5", "1.5"));
        PythonRepr.Repr(store.ToDict()).Should().Be(
            "{'profiles': [{'name': '5', 'description': None, 'tags': [], 'metadata': {}, 'configs': [{'id': '1.5', 'path': None, "
            + "'path_glob': None, 'application': '2', 'version': 'True', 'branch': None, 'tags': [], 'expected_format': None, "
            + "'expected_variant': None, 'metadata': {}}]}]}");
    }

    [Fact]
    public void LoadJsonRaisesPythonsFriendlyErrors()
    {
        var missing = Path.Combine(_tmp.FullName, "missing.json");
        var readMissing = () => DetectionProfileCommands.LoadJson(missing);
        readMissing.Should().Throw<PythonValueException>()
            .WithMessage($"Unable to read JSON payload from {missing}: [Errno 2] No such file or directory: '{missing}'");

        var directory = Directory.CreateDirectory(Path.Combine(_tmp.FullName, "sub")).FullName;
        var readDirectory = () => DetectionProfileCommands.LoadJson(directory + "/");
        readDirectory.Should().Throw<PythonValueException>()
            .WithMessage($"Unable to read JSON payload from {directory}: {OSErrorTexts.DirectoryOpen(directory)}");

        var bad = Path.Combine(_tmp.FullName, "bad.json");
        File.WriteAllBytes(bad, [0xFF, (byte)'{', (byte)'}']);
        var readBad = () => DetectionProfileCommands.LoadJson(bad);
        readBad.Should().Throw<PythonUnicodeDecodeException>().WithMessage("'utf-8' codec can't decode byte 0xff in position 0: invalid start byte");

        var truncated = Write("trunc.json", "{\"a\": ");
        var readTruncated = () => DetectionProfileCommands.LoadJson(truncated);
        readTruncated.Should().Throw<PythonValueException>().Which.Message.Should().StartWith($"Failed to parse JSON from {truncated}: ");

        var bom = Path.Combine(_tmp.FullName, "bom.json");
        File.WriteAllBytes(bom, [0xEF, 0xBB, 0xBF, (byte)'{', (byte)'}']);
        var readBom = () => DetectionProfileCommands.LoadJson(bom);
        readBom.Should().Throw<PythonValueException>();

        Write("store.json", """{"profiles": []}""");
        Map(DetectionProfileCommands.LoadJson(_tmp.FullName + "//store.json"))["profiles"].Should().BeOfType<List<object?>>();
    }

    [Fact]
    public void HuntBridgeResolvesPathsAgainstTheRootAndSkipsNonDictHits()
    {
        var storePath = Write("store.json", """{"profiles": [{"name": "prod", "configs": [{"id": "c", "path_glob": "sub/*.json"}]}]}""");
        var root = _tmp.FullName;
        var x = JsonSerializer.Serialize(Path.Combine(root, "sub", "x.json"));
        var y = JsonSerializer.Serialize(Path.Combine(root, "sub", "..", "sub", "y.json"));
        var huntPath = Write(
            "hunts.json",
            $$"""[{"path": {{x}}}, {"path": {{y}}}, {"relative_path": "", "path": "other/z.json"}, {"relative_path": "sub/w.json", "path": 5}, 5]""");

        var payload = DetectionProfileCommands.HuntBridge(storePath, huntPath, tags: null, root: root);

        var items = Items(payload["items"]);
        items.Select(item => Map(item)["relative_path"]).Should().Equal("sub/x.json", "sub/../sub/y.json", "z.json", "sub/w.json");
        items.Select(item => Items(Map(item)["profiles"]).Count).Should().Equal(1, 1, 0, 1);
        Map(Items(Map(items[0])["profiles"])[0]).Keys.Should().Equal("profile", "config", "profile_tags", "expected_format", "expected_variant");
        Map(items[0]).Keys.Should().Equal("hunt", "relative_path", "profiles");

        var stringHunts = Write("str.json", "\"hits\"");
        Items(DetectionProfileCommands.HuntBridge(storePath, stringHunts, tags: null, root: null)["items"]).Should().BeEmpty();

        var dictHunts = Write("dict.json", """{"a": 1}""");
        var dictPayload = () => DetectionProfileCommands.HuntBridge(storePath, dictHunts, tags: null, root: null);
        dictPayload.Should().Throw<PythonValueException>().WithMessage("Hunt payload must be a JSON array of hunt hits.");
    }
    // CPython 3.13 profile_cli._resolve_relative_path over paths whose last part is ".".
    [Theory]
    [InlineData("dir/file.config/.", null, "file.config")]
    [InlineData("a/./", null, "a")]
    [InlineData("/r/x/.", "/r", "x")]
    [InlineData("/r/.", "/r", ".")]
    [InlineData("/other/y/.", "/r", "y")]
    [InlineData("./", null, "")]
    public void ResolveRelativePathDropsDotParts(string path, string? root, string expected)
        => DetectionProfileCommands.ResolveRelativePath(new Dictionary<string, object?>(StringComparer.Ordinal) { ["path"] = path }, root).Should().Be(expected);
}
