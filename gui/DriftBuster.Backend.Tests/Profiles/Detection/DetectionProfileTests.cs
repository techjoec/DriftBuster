using System.Text.Json;

using DriftBuster.Backend.Json;
using DriftBuster.Backend.Profiles.Detection;

namespace DriftBuster.Backend.Tests.Profiles.Detection;

/// <summary>Detection profile stores: strict reads, matching, summary, summary diff and the hunt bridge.</summary>
public sealed class DetectionProfileTests : IDisposable
{
    private const string Store = """
        {"profiles": [
          {"name": "prod-web", "tags": ["env:prod", "tier:web"], "configs": [
            {"id": "web-config", "path": "web/web.config", "expected_format": "xml"},
            {"id": "logs", "path_glob": "logs/*.log", "application": "shop"}]},
          {"name": "any", "configs": [{"id": "everywhere"}]}
        ]}
        """;

    private readonly DirectoryInfo _tmp = Directory.CreateTempSubdirectory("driftbuster-detection-profiles-");

    public void Dispose() => _tmp.Delete(recursive: true);

    private string Write(string name, string text)
    {
        var path = Path.Join(_tmp.FullName, name);
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public void Configs_match_by_tags_path_and_glob()
    {
        var store = DetectionProfileStore.Load(Write("store.json", Store));
        HashSet<string> Tags(params string[] tags) => new(tags, StringComparer.Ordinal);

        store.MatchingConfigs(Tags("env:prod", "tier:web"), "web/web.config").Select(match => match.Config.Id).Should().Equal("web-config", "everywhere");
        store.MatchingConfigs(Tags("env:prod"), "web/web.config").Select(match => match.Config.Id).Should().Equal("everywhere");
        store.MatchingConfigs(Tags("env:prod", "tier:web"), @"logs\a.log").Should().ContainSingle(match => match.Config.Id == "everywhere");
        store.MatchingConfigs(Tags("env:prod", "tier:web", "application:shop"), "logs/a.log").Select(match => match.Config.Id).Should().Equal("logs", "everywhere");
        store.MatchingConfigs(Tags(), null).Select(match => match.Config.Id).Should().Equal("everywhere");
    }

    [Theory]
    [InlineData("""{"profiles": [{"name": "a", "configs": [{"id": "x", "colour": 1}]}]}""", "$.profiles[0].configs[0].colour")]
    [InlineData("""{"profiles": [{"configs": []}]}""", "name")]
    [InlineData("""{"profiles": [{"name": "a", "tags": "prod"}]}""", "$.profiles[0].tags")]
    public void A_store_the_model_does_not_describe_is_refused_with_its_path(string json, string where)
    {
        var path = Write("bad.json", json);
        FluentActions.Invoking(() => DetectionProfileStore.Load(path))
            .Should().Throw<DetectionProfileException>().Where(exc => exc.Message.StartsWith(path + ": ", StringComparison.Ordinal) && exc.Message.Contains(where, StringComparison.Ordinal));
    }

    [Fact]
    public void Names_and_config_ids_must_be_unique()
    {
        var config = new DetectionProfileConfig { Id = "x" };
        FluentActions.Invoking(() => new DetectionProfileStore([new DetectionProfile { Name = "a" }, new DetectionProfile { Name = "a" }]))
            .Should().Throw<DetectionProfileException>().WithMessage("Profile 'a' is defined twice.");
        FluentActions.Invoking(() => new DetectionProfileStore([new DetectionProfile { Name = "a", Configs = [config] }, new DetectionProfile { Name = "b", Configs = [config] }]))
            .Should().Throw<DetectionProfileException>().WithMessage("Config id 'x' in profile 'b' is already used by profile 'a'.");
    }

    [Fact]
    public void Summaries_diff_by_profile_and_config_id()
    {
        var before = Write("before.json", ModelJson.Serialize(DetectionProfileStore.Load(Write("store.json", Store)).Summary()));
        var after = Write("after.json", ModelJson.Serialize(new DetectionProfileStore(
        [
            new DetectionProfile { Name = "prod-web", Configs = [new DetectionProfileConfig { Id = "web-config" }, new DetectionProfileConfig { Id = "app" }] },
            new DetectionProfile { Name = "new" },
        ]).Summary()));

        var summary = DetectionProfileCommands.Summary(Write("store2.json", Store));
        summary.Should().BeEquivalentTo(new DetectionProfileSummary(2, 3,
        [
            new DetectionProfileSummaryEntry("any", null, [], ["everywhere"]),
            new DetectionProfileSummaryEntry("prod-web", null, ["env:prod", "tier:web"], ["web-config", "logs"]),
        ]));
        DetectionProfileCommands.Diff(before, after).Should().BeEquivalentTo(new DetectionProfileSummaryDiff(
            new DetectionProfileSummaryTotals(2, 3),
            new DetectionProfileSummaryTotals(2, 2),
            ["new"],
            ["any"],
            [new DetectionProfileChange("prod-web", 2, 2, ["app"], ["logs"])]));
    }

    [Fact]
    public void Hunt_hits_get_the_configs_their_paths_match()
    {
        var store = Write("store.json", Store);
        var hunt = Write("hunt.json", """[{"path": "/srv/app/web/web.config", "line_number": 3}, {"relative_path": "other.txt"}, 5]""");

        var bridge = DetectionProfileCommands.HuntBridge(store, hunt, ["env:prod", "tier:web", " "], "/srv/app");

        bridge.Items.Select(item => item.RelativePath).Should().Equal("web/web.config", "other.txt", null);
        bridge.Items[0].Profiles.Select(match => match.Config).Should().Equal("web-config", "everywhere");
        bridge.Items[0].Profiles[0].ProfileTags.Should().Equal("env:prod", "tier:web");
        bridge.Items[0].Hunt.GetProperty("line_number").GetInt32().Should().Be(3);
        FluentActions.Invoking(() => DetectionProfileCommands.HuntBridge(store, Write("object.json", "{}"), null, null))
            .Should().Throw<DetectionProfileException>().WithMessage("*a hunt file holds a JSON array of hits.");
        ModelJson.Serialize(bridge).Should().Contain("\"relative_path\": \"web/web.config\"");
    }
}
