using DriftBuster.Backend.Curation;
using DriftBuster.Backend.Models;

namespace DriftBuster.Backend.Tests.Curation;

/// <summary>Curation applied to a comparison: ignores, masks, rules, groups, the review list, scopes, and the recount.</summary>
// The sample's password is masked by the process-wide secret rules that the secret scanner tests replace.
public sealed class CurationApplierTests
{
    private static readonly string HostSet = CurationScopes.HostSetId(CurationSample.Plans);

    private static FileComparison App(SettingsComparison comparison) => comparison.Files.Single(file => file.Path.EndsWith("app.json", StringComparison.Ordinal));

    private static SettingRow Row(SettingsComparison comparison, string key) => App(comparison).Settings.Single(row => string.Equals(row.Key, key, StringComparison.Ordinal));

    private static SettingsComparison Apply(CurationDocument document, params CurationChoice[] session) =>
        CurationApplier.Apply(CurationSample.Build(), document, session, HostSet);

    [Fact]
    public void Without_curation_the_counts_are_the_builders()
    {
        var source = CurationSample.Build();
        var curated = CurationApplier.Apply(source, new CurationDocument(), [], HostSet);

        curated.Hosts.Select(host => (host.SettingsDiffering, host.FilesDiffering, host.MatchesBaseline))
            .Should().Equal(source.Hosts.Select(host => (host.SettingsDiffering, host.FilesDiffering, host.MatchesBaseline)));
        App(curated).SettingsDiffering.Should().Be(App(source).SettingsDiffering);
    }

    [Fact]
    public void Ignoring_a_setting_takes_it_out_of_every_count()
    {
        var curated = Apply(new CurationDocument(), new CurationChoice { Target = new CurationTarget { File = "*app.json", Key = "Built" } });

        Row(curated, "Built").Ignored.Should().BeTrue();
        Row(curated, "Built").Differs.Should().BeFalse();
        curated.Hosts[1].MatchesBaseline.Should().BeTrue("staging only differed in the ignored timestamp");
        curated.Hosts[2].SettingsDiffering.Should().Be(2, "Cache.Minutes and the password still differ on prod");
    }

    [Fact]
    public void Ignoring_one_value_keeps_other_values_of_the_setting()
    {
        var prodValue = Row(CurationSample.Build(), "Cache.Minutes").Values[2].ValueHash!;
        var curated = Apply(new CurationDocument(), new CurationChoice
        {
            Target = new CurationTarget { File = "apps/web/app.json", Key = "Cache.Minutes", ValueHash = prodValue },
        });

        Row(curated, "Cache.Minutes").Values[2].Ignored.Should().BeTrue();
        Row(curated, "Cache.Minutes").Differs.Should().BeFalse();
        Row(curated, "Built").Differs.Should().BeTrue();
    }

    [Fact]
    public void Ignoring_a_source_takes_the_whole_file_out()
    {
        var curated = Apply(new CurationDocument
        {
            Choices = [new CurationChoice { Target = new CurationTarget { File = "legacy.json" } }],
        });

        var legacy = curated.Files.Single(file => string.Equals(file.Path, "legacy.json", StringComparison.Ordinal));
        legacy.Ignored.Should().BeTrue();
        legacy.Differs.Should().BeFalse();
        curated.Hosts[2].FilesMissing.Should().BeEmpty();
    }

    [Fact]
    public void Masks_and_unmasks_apply_in_order_and_the_latest_wins()
    {
        var unmask = new CurationChoice { Target = new CurationTarget { Key = "db.password" }, Kind = CurationChoiceKinds.Unmask };
        var unmasked = Apply(new CurationDocument { Choices = [unmask] });
        Row(unmasked, "db.password").Values.Select(value => value.Value).Should().Equal("one", "one", "two");
        Row(unmasked, "db.password").Values.Should().OnlyContain(value => !value.Masked);

        var mask = new CurationChoice { Target = new CurationTarget { Key = "Cache.*" }, Kind = CurationChoiceKinds.Mask };
        var masked = Apply(new CurationDocument { Choices = [unmask] }, mask, new CurationChoice { Target = new CurationTarget { Key = "db.password" }, Kind = CurationChoiceKinds.Mask });
        Row(masked, "Cache.Minutes").Values.Should().OnlyContain(value => value.Masked && value.Value == null);
        Row(masked, "Cache.Minutes").Differs.Should().BeTrue("masked values are still compared");
        Row(masked, "db.password").Values.Should().OnlyContain(value => value.Masked, "this run's mask comes after the saved unmask");
    }

    [Fact]
    public void Rules_label_files_and_act_on_matching_settings()
    {
        var curated = Apply(new CurationDocument
        {
            Rules =
            [
                new CurationRule { Name = "web app", FilePattern = "apps/web/*", AppName = "Contoso Web", FileLabel = "Web settings", Description = "Front end" },
                new CurationRule { Name = "build stamps", KeyPattern = "Built", Ignore = true, Groups = ["Noise"] },
                new CurationRule { Name = "off", KeyPattern = "*", Ignore = true, Enabled = false },
            ],
        });

        var app = App(curated);
        (app.AppName, app.FileLabel, app.Description).Should().Be(("Contoso Web", "Web settings", "Front end"));
        Row(curated, "Built").Ignored.Should().BeTrue();
        Row(curated, "Built").Groups.Should().Equal("Noise");
        Row(curated, "Cache.Minutes").Ignored.Should().BeFalse("the disabled rule does nothing");
    }

    [Fact]
    public void Groups_review_items_and_scopes()
    {
        var curated = Apply(new CurationDocument
        {
            Groups =
            [
                new CurationGroup { Name = "Caching", Members = [new CurationTarget { Key = "Cache.*" }] },
                new CurationGroup { Name = "Web files", Members = [new CurationTarget { File = "apps/*" }] },
                new CurationGroup { Name = "Other servers", Scope = "hosts-000000000000", Members = [new CurationTarget { Key = "*" }] },
            ],
            Review = [new CurationReviewItem { Target = new CurationTarget { File = "apps/web/app.json", Key = "Cache.Minutes" } }],
            Choices = [new CurationChoice { Target = new CurationTarget { Key = "Cache.Minutes" }, Scope = "hosts-000000000000" }],
        });

        Row(curated, "Cache.Minutes").Groups.Should().Equal("Caching", "Web files");
        Row(curated, "Built").Groups.Should().Equal("Web files");
        Row(curated, "Cache.Minutes").InReview.Should().BeTrue();
        Row(curated, "Built").InReview.Should().BeFalse();
        Apply(new CurationDocument { Review = [new CurationReviewItem { Target = new CurationTarget { File = "apps/web/app.json" } }] })
            .Files.Single(file => file.Path.EndsWith("app.json", StringComparison.Ordinal)).Settings.Should().OnlyContain(row => row.InReview, "a file on the review list puts all its settings there");
        Row(curated, "Cache.Minutes").Ignored.Should().BeFalse("the ignore belongs to another host set");
    }

    [Fact]
    public void The_source_comparison_is_left_untouched()
    {
        var source = CurationSample.Build();
        CurationApplier.Apply(source, new CurationDocument(), [new CurationChoice { Target = new CurationTarget { Key = "*" } }], HostSet);

        App(source).Settings.Should().OnlyContain(row => !row.Ignored);
        App(source).SettingsDiffering.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Host_set_ids_are_stable_and_order_free()
    {
        var reversed = CurationSample.Plans.Reverse().ToArray();
        CurationScopes.HostSetId(reversed).Should().Be(HostSet);
        HostSet.Should().StartWith("hosts-").And.HaveLength(18);
        CurationScopes.Applies(CurationScopes.AllRuns, HostSet).Should().BeTrue();
        CurationScopes.Applies("hosts-other", HostSet).Should().BeFalse();
    }

    [Fact]
    public void Patterns_are_case_insensitive_wildcards_and_empty_matches_all()
    {
        CurationPattern.IsMatch("Apps/Web/App.json", "apps/*/app.JSON").Should().BeTrue();
        CurationPattern.IsMatch("anything", string.Empty).Should().BeTrue();
        CurationPattern.IsMatch(@"C:\path\x", @"C:\path\*").Should().BeTrue();
        CurationPattern.IsMatch("Cache.Minutes", "Cache.Hours").Should().BeFalse();
        CurationPattern.IsMatch("Built", "Cache.*; Built").Should().BeTrue("; separates alternatives");
        CurationPattern.IsMatch("Other", "Cache.*;Built").Should().BeFalse();
    }
}
