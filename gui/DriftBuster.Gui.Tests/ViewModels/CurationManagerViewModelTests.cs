using DriftBuster.Backend.Curation;
using DriftBuster.Gui.Tests.Fakes;
using DriftBuster.Gui.ViewModels;

namespace DriftBuster.Gui.Tests.ViewModels;

/// <summary>Manage choices: loading, editing and saving groups, rules, choices and the review list.</summary>
public sealed class CurationManagerViewModelTests
{
    private static InMemoryCurationService Seeded()
    {
        var curation = new InMemoryCurationService();
        curation.Update(_ => new CurationDocument
        {
            Groups = [new CurationGroup { Name = "Caching", Members = [new CurationTarget { File = "a.json", Key = "Cache.*" }, new CurationTarget { File = "b.json" }] }],
            Rules = [new CurationRule { Name = "stamps", KeyPattern = "Built", Mask = CurationChoiceKinds.Unmask, Groups = ["Noise", "noise"], Scope = "hosts-old" }],
            Choices =
            [
                new CurationChoice { Target = new CurationTarget { Key = "x", ValueHash = "0123456789abcdef" } },
                new CurationChoice { Target = new CurationTarget { Key = "y" }, Kind = CurationChoiceKinds.Mask, Scope = "hosts-1" },
            ],
            Review = [new CurationReviewItem { Target = new CurationTarget { File = "a.json", Key = "k" } }],
        });
        return curation;
    }

    [Fact]
    public void Loads_everything_in_words()
    {
        var manager = new CurationManagerViewModel(Seeded(), "hosts-1", CurationManagerViewModel.ChoicesTab);

        manager.SelectedTab.Should().Be(CurationManagerViewModel.ChoicesTab);
        manager.SelectedGroup!.Members.Select(member => member.Text).Should().Equal("Cache.* in a.json", "the whole file b.json");
        manager.SelectedRule!.MaskOption.Should().Be("Show values");
        manager.SelectedRule.TheseServersOnly.Should().BeTrue();
        manager.SelectedRule.Groups.Should().Be("Noise, noise");
        manager.Choices.Select(choice => choice.Text).Should().Equal("Ignore x = value 01234567… in any file (every run)", "Mask y in any file (these servers only)");
        manager.Review.Single().Text.Should().Be("k in a.json");
        manager.IsDirty.Should().BeFalse();
        manager.HistoryText.Should().StartWith("No scans recorded yet");
    }

    [Fact]
    public void Edits_are_saved_together()
    {
        var curation = Seeded();
        var manager = new CurationManagerViewModel(curation, "hosts-1");

        manager.RemoveMemberCommand.Execute(manager.SelectedGroup!.Members[1]);
        manager.SelectedGroup.Name = "Cache settings";
        manager.SelectedRule!.MaskOption = RuleEditViewModel.MaskOptions[1];
        manager.SelectedRule.Ignore = true;
        manager.RemoveChoiceCommand.Execute(manager.Choices[0]);
        manager.RemoveReviewCommand.Execute(manager.Review[0]);
        manager.AddRuleCommand.Execute(null);
        manager.SelectedRule!.Name = "new";
        manager.SelectedRule.TheseServersOnly = true;
        manager.IsDirty.Should().BeTrue();

        manager.SaveCommand.Execute(null);

        var document = curation.Document;
        document.Groups.Single().Should().BeEquivalentTo(new CurationGroup { Name = "Cache settings", Members = [new CurationTarget { File = "a.json", Key = "Cache.*" }] });
        document.Rules[0].Should().BeEquivalentTo(new CurationRule { Name = "stamps", KeyPattern = "Built", Mask = CurationChoiceKinds.Mask, Ignore = true, Groups = ["Noise"], Scope = "hosts-old" });
        document.Rules[1].Scope.Should().Be("hosts-1", "a new rule for these servers gets this host set");
        document.Choices.Should().ContainSingle().Which.Kind.Should().Be(CurationChoiceKinds.Mask);
        document.Review.Should().BeEmpty();
        manager.IsDirty.Should().BeFalse();
        manager.StatusMessage.Should().Be("Saved.");
    }

    [Fact]
    public void A_new_rule_draft_is_selected_and_unsaved()
    {
        var manager = new CurationManagerViewModel(new InMemoryCurationService(), "hosts-1", CurationManagerViewModel.RulesTab, new CurationRule { Name = "draft", KeyPattern = "k" });

        manager.SelectedRule!.Name.Should().Be("draft");
        manager.IsDirty.Should().BeTrue();

        manager.DeleteRuleCommand.Execute(manager.SelectedRule);
        manager.DeleteGroupCommand.Execute(null);
        manager.Rules.Should().BeEmpty();
    }

    [Fact]
    public void History_is_cleared_and_curation_moves_in_and_out()
    {
        var curation = Seeded();
        var manager = new CurationManagerViewModel(curation, "hosts-1");

        manager.ClearHistory();
        manager.Export("/tmp/out.json");
        manager.Import("/tmp/in.json", replace: true);

        curation.Cleared.Should().BeTrue();
        curation.Exported.Should().Be("/tmp/out.json");
        manager.Groups.Should().BeEmpty("the fake's replace imports an empty document");
        manager.StatusMessage.Should().Be("Replaced with /tmp/in.json.");
    }

    [Fact]
    public void Settings_trees_nest_keys_and_show_values()
    {
        var viewModel = new CompareViewModel(new InMemoryCurationService());
        viewModel.Load(SampleComparison.Build());
        viewModel.DifferencesOnly = false;

        var tree = viewModel.TreeOf(viewModel.VisibleFiles[0]);

        tree.Select(node => node.Name).Should().Equal("Cache", "Logging", "db");
        tree[0].Children.Single().ValuesText.Should().Be("baseline: 15  ·  staging: 15  ·  prod: 60");
        tree[0].Differs.Should().BeTrue();
        tree[1].Differs.Should().BeFalse();
        tree[0].ValuesText.Should().BeEmpty();
    }
}
