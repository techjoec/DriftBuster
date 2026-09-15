using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Profiles.Detection;

namespace DriftBuster.Backend.Tests.Profiles.Detection;

/// <summary>
/// Mirror of tests/core/test_profiles.py. Python's <c>pytest.raises(TypeError)</c> for a mutator that is not callable or returns
/// something other than a profile is a null mutator or a null result here, the only non-profiles the typed delegate admits.
/// </summary>
public sealed class ProfilesTests
{
    private static readonly HashSet<string> NoTags = new(StringComparer.Ordinal);

    private static HashSet<string> Tags(params string[] tags) => new(tags, StringComparer.Ordinal);

    private static OrderedDictionary<string, object?> Map(object? value) => value.Should().BeOfType<OrderedDictionary<string, object?>>().Subject;

    private static List<object?> Items(object? value) => value.Should().BeOfType<List<object?>>().Subject;

    [Fact]
    public void ProfileConfigMatchingRules()
    {
        var config = new DetectionProfileConfig("cfg-web", path: "configs/web.config", application: "web", tags: ["prod"]);

        var matchingTags = Tags("prod", "application:web");
        config.Matches("configs/web.config", matchingTags).Should().BeTrue();

        config.Matches("configs/web.config", NoTags).Should().BeFalse();
        config.Matches("other.config", matchingTags).Should().BeFalse();
    }

    [Fact]
    public void ProfileStoreRegistrationAndMatching()
    {
        var prodProfile = new DetectionProfile(
            "prod",
            tags: ["prod"],
            configs: [new DetectionProfileConfig("cfg-app", path: "appsettings.json", tags: ["prod"], application: "api")]);

        var store = new DetectionProfileStore([prodProfile]);
        var matches = store.MatchingConfigs(["prod", "application:api"], relativePath: "appsettings.json");

        matches.Should().NotBeEmpty();
        var applied = matches[0];
        applied.Should().BeOfType<AppliedProfileConfig>();
        applied.Profile.Name.Should().Be("prod");
        applied.Config.Identifier.Should().Be("cfg-app");

        var duplicateProfile = () => store.RegisterProfile(prodProfile);
        duplicateProfile.Should().Throw<PythonValueException>();

        var duplicateConfig = () => store.RegisterProfile(new DetectionProfile("dupe-config", configs: [new DetectionProfileConfig("cfg-app")]));
        duplicateConfig.Should().Throw<PythonValueException>();
    }

    [Fact]
    public void ProfileStoreSummaryAndDiff()
    {
        var baselineStore = new DetectionProfileStore([new DetectionProfile("prod", configs: [new DetectionProfileConfig("cfg1")])]);
        var currentStore = new DetectionProfileStore(
        [
            new DetectionProfile("prod", configs: [new DetectionProfileConfig("cfg1"), new DetectionProfileConfig("cfg2")]),
            new DetectionProfile("staging", configs: [new DetectionProfileConfig("cfg3")]),
        ]);

        var summary = baselineStore.Summary();
        summary["total_profiles"].Should().Be(1);
        summary["total_configs"].Should().Be(1);

        var diff = DetectionProfileStore.DiffSummarySnapshots(baselineStore.Summary(), currentStore.Summary());
        Items(diff["added_profiles"]).Should().Equal("staging");
        Items(diff["removed_profiles"]).Should().BeEmpty();
        Map(Map(diff["totals"])["current"])["profiles"].Should().Be(2);
        var changed = Items(diff["changed_profiles"]);
        changed.Should().HaveCount(1);
        Map(changed[0])["name"].Should().Be("prod");
        Items(Map(changed[0])["added_config_ids"]).Should().Equal("cfg2");
    }

    [Fact]
    public void ProfileStoreUpdateRemoveAndSerialisation()
    {
        var profile = new DetectionProfile("default", configs: [new DetectionProfileConfig("cfg1"), new DetectionProfileConfig("cfg2")]);
        var store = new DetectionProfileStore([profile]);

        var notCallable = () => store.UpdateProfile("default", mutator: null);
        notCallable.Should().Throw<PythonTypeException>();

        static DetectionProfile Mutate(DetectionProfile original) => new(original.Name, configs: original.Configs.Take(original.Configs.Count - 1));

        var updated = store.UpdateProfile("default", Mutate);
        updated.Configs.Should().HaveCount(1);

        var missing = () => store.RemoveConfig("default", "cfg-missing");
        missing.Should().Throw<PythonValueException>();

        store.RemoveConfig("default", "cfg1");
        store.FindConfig("cfg1").Should().BeEmpty();

        var payload = store.ToDict();
        Items(payload["profiles"]).Should().HaveCount(1);
        var exported = Map(Items(payload["profiles"])[0]);
        Items(exported["configs"]).Should().BeEmpty();

        PythonJson.TryLoads(
            """{"profiles": [{"name": "imported", "configs": [{"id": "cfg", "path": "path\\file.txt", "tags": [" prod "]}]}]}""",
            out var imported).Should().BeTrue();
        var rebuilt = DetectionProfileStore.FromDict(imported);
        rebuilt.FindConfig("cfg").Should().NotBeEmpty();
    }

    [Fact]
    public void ApplicableProfilesAndNormaliseTags()
    {
        var profile = new DetectionProfile("tagged", tags: ["prod"]);
        var store = new DetectionProfileStore([profile]);

        var matches = store.ApplicableProfiles(["prod"]);
        matches[0].Name.Should().Be("tagged");

        store.ApplicableProfiles(["dev"]).Should().BeEmpty();
        ProfileTags.Normalize([" prod ", ""]).Should().BeEquivalentTo(["prod"]);
    }

    private static DetectionProfileConfig Config(string identifier, string? path = null, string? pathGlob = null, IEnumerable<string>? tags = null)
        => new(identifier, path: path, pathGlob: pathGlob, tags: tags ?? []);

    [Fact]
    public void ProfileConfigMatchesTaggedAndGlobPaths()
    {
        var config = Config("cfg", path: "configs/app.config", tags: ["prod"]);
        var other = Config("glob", pathGlob: "configs/*.json");
        var tags = ProfileTags.Normalize(["prod", "application:demo"]);

        config.Matches("configs/app.config", tags).Should().BeTrue();
        config.Matches("configs/app.config", NoTags).Should().BeFalse();
        config.Matches(null, tags).Should().BeFalse();
        other.Matches("configs/settings.json", NoTags).Should().BeTrue();
        other.Matches("other.json", NoTags).Should().BeFalse();

        var applicationConfig = new DetectionProfileConfig("app", application: "service");
        applicationConfig.Matches(null, Tags("application:service")).Should().BeTrue();
        applicationConfig.Matches(null, NoTags).Should().BeFalse();
    }

    [Fact]
    public void ConfigurationProfileMatchingConfigsRespectsPathFilters()
    {
        var config = Config("cfg", path: "service.json", tags: ["svc"]);
        var profile = new DetectionProfile("svc", configs: [config], tags: ["svc"]);
        var matched = profile.MatchingConfigs(Tags("svc"), "service.json");
        matched.Should().Equal(config);
        profile.MatchingConfigs(NoTags, "service.json").Should().BeEmpty();
    }

    [Fact]
    public void ProfileStoreUpdateMutatorValidation()
    {
        var profile = new DetectionProfile("demo", configs: [Config("cfg")]);
        var store = new DetectionProfileStore([profile]);

        var notCallable = () => store.UpdateProfile("demo", mutator: null);
        notCallable.Should().Throw<PythonTypeException>();

        var invalid = () => store.UpdateProfile("demo", _ => null!);
        invalid.Should().Throw<PythonTypeException>();

        static DetectionProfile Rename(DetectionProfile original) => original with { Name = "other" };

        store.RegisterProfile(new DetectionProfile("other", configs: []));
        var clash = () => store.UpdateProfile("demo", Rename);
        clash.Should().Throw<PythonValueException>();
    }

    [Fact]
    public void ProfileStoreUpdateRevertsOnFailure()
    {
        var original = new DetectionProfile("demo", configs: [Config("cfg")]);
        var store = new DetectionProfileStore([original]);

        static DetectionProfile BadMutator(DetectionProfile profile)
        {
            var newConfig = Config("cfg");
            return profile with { Configs = [newConfig, newConfig] };
        }

        var act = () => store.UpdateProfile("demo", BadMutator);
        act.Should().Throw<PythonValueException>();

        store.GetProfile("demo").Configs.Should().Equal(original.Configs);
    }

    [Fact]
    public void ProfileStoreRemoveProfileAndConfig()
    {
        var profile = new DetectionProfile("demo", configs: [Config("cfg"), Config("other")]);
        var store = new DetectionProfileStore([profile]);

        var updated = store.RemoveConfig("demo", "other");
        updated.Configs.Should().HaveCount(1);

        var missingProfile = () => store.RemoveConfig("missing", "cfg");
        missingProfile.Should().Throw<KeyNotFoundException>();

        var missingConfig = () => store.RemoveConfig("demo", "missing");
        missingConfig.Should().Throw<PythonValueException>();

        store.RemoveProfile("demo");
        store.FindConfig("cfg").Should().BeEmpty();
    }

    [Fact]
    public void DiffSummarySnapshotsSkipsEntriesWithoutName()
    {
        var baseline = new OrderedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["profiles"] = new List<object?>
            {
                new OrderedDictionary<string, object?>(StringComparer.Ordinal) { ["name"] = "demo", ["config_ids"] = new List<object?> { "a" } },
                new OrderedDictionary<string, object?>(StringComparer.Ordinal) { ["config_ids"] = new List<object?> { "b" } },
            },
        };
        var current = new OrderedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["profiles"] = new List<object?>
            {
                new OrderedDictionary<string, object?>(StringComparer.Ordinal) { ["name"] = "demo", ["config_ids"] = new List<object?> { "a", "b" } },
            },
        };
        var result = DetectionProfileStore.DiffSummarySnapshots(baseline, current);
        Map(Map(result["totals"])["current"])["configs"].Should().Be(2);
    }

    [Fact]
    public void ProfileStoreMatchingConfigsReturnsIndexedResults()
    {
        var profile = new DetectionProfile("demo", configs: [Config("cfg")]);
        var store = new DetectionProfileStore([profile]);
        var result = store.MatchingConfigs(tags: null, relativePath: "whatever");
        result[0].Should().BeOfType<AppliedProfileConfig>();
        store.FindConfig("missing").Should().BeEmpty();
    }
}
