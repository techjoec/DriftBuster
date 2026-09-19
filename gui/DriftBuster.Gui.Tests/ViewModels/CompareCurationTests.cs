using DriftBuster.Backend.Curation;
using DriftBuster.Backend.History;
using DriftBuster.Gui.Tests.Fakes;
using DriftBuster.Gui.ViewModels;

namespace DriftBuster.Gui.Tests.ViewModels;

/// <summary>The Compare view's right-click actions: ignore, mask, marks, groups, rules, the review list, history and copies.</summary>
public sealed class CompareCurationTests
{
    private static (CompareViewModel ViewModel, InMemoryCurationService Curation) Load()
    {
        var curation = new InMemoryCurationService();
        var viewModel = new CompareViewModel(curation) { HostSetId = "hosts-test" };
        viewModel.Load(SampleComparison.Build());
        return (viewModel, curation);
    }

    private static CompareContext Setting(CompareViewModel viewModel, string key, int cell = -1)
    {
        var file = viewModel.VisibleFiles.First(candidate => candidate.AllRows.Any(row => string.Equals(row.Key, key, StringComparison.Ordinal)));
        var row = file.AllRows.Single(candidate => string.Equals(candidate.Key, key, StringComparison.Ordinal));
        return new CompareContext(file, row, cell >= 0 ? row.Cells[cell] : null);
    }

    [Fact]
    public void Ignoring_a_setting_for_this_run_hides_it_and_recounts()
    {
        var (viewModel, curation) = Load();

        viewModel.Ignore(Setting(viewModel, "Cache.Minutes"), CompareTargetLevel.Setting, CurationPersistence.ThisRun);

        curation.Saves.Should().Be(0, "this run's choices are not saved");
        viewModel.VisibleFiles[0].VisibleRows.Select(row => row.Key).Should().Equal("db.password");
        viewModel.Servers[2].Summary.Should().Be("1 setting differs in 1 file, 1 file missing");
        viewModel.StatusMessage.Should().Be("Ignoring Cache.Minutes until the app closes.");

        viewModel.ShowIgnored = true;
        viewModel.VisibleFiles[0].VisibleRows.Should().Contain(row => row.Ignored && row.Key == "Cache.Minutes");

        viewModel.ClearRunChoices();
        viewModel.StatusMessage.Should().Be("Forgot this run's ignore and mask choices.");
        viewModel.ShowIgnored = false;
        viewModel.VisibleFiles[0].VisibleRows.Should().HaveCount(2);
    }

    [Fact]
    public void Always_choices_are_saved_with_their_scope()
    {
        var (viewModel, curation) = Load();

        viewModel.Ignore(Setting(viewModel, "Cache.Minutes", 2), CompareTargetLevel.Value, CurationPersistence.AllRuns);
        viewModel.Ignore(new CompareContext(viewModel.VisibleFiles[1]), CompareTargetLevel.Source, CurationPersistence.TheseServers);

        var choices = curation.Document.Choices;
        choices.Should().HaveCount(2);
        choices[0].Target.ValueHash.Should().Be(CurationTarget.ValueHashOf("60"));
        choices[0].Note.Should().Be("\"60\"");
        new CurationChoiceItem(choices[0]).Text.Should().Be("Ignore Cache.Minutes = \"60\" in inetpub/app/appsettings.json (every run)");
        choices[0].Scope.Should().BeEmpty();
        choices[1].Target.Should().Be(new CurationTarget { File = "legacy.ini" });
        choices[1].Scope.Should().Be("hosts-test");
        viewModel.VisibleFiles.Select(file => file.Path).Should().Equal("inetpub/app/appsettings.json");
        viewModel.StatusMessage.Should().Be("Ignoring legacy.ini whenever these servers are compared.");

        viewModel.ShowIgnored = true;
        var legacy = viewModel.VisibleFiles.Single(file => string.Equals(file.Path, "legacy.ini", StringComparison.Ordinal));
        legacy.Summary.Should().Be("ignored: left out of the differences");
        legacy.AllRows.Should().OnlyContain(row => row.Ignored, "settings in an ignored file show as ignored");
    }

    [Fact]
    public void Masks_and_unmasks_replace_each_other()
    {
        var (viewModel, curation) = Load();

        viewModel.SetMasked(Setting(viewModel, "db.password"), masked: false, CurationPersistence.ThisRun);
        Setting(viewModel, "db.password").Row!.Cells.Select(cell => cell.Text).Should().Equal("one", "one", "two");

        viewModel.SetMasked(Setting(viewModel, "Cache.Minutes"), masked: true, CurationPersistence.AllRuns);
        viewModel.SetMasked(Setting(viewModel, "Cache.Minutes"), masked: false, CurationPersistence.AllRuns);
        curation.Document.Choices.Should().ContainSingle().Which.Kind.Should().Be(CurationChoiceKinds.Unmask);
        viewModel.StatusMessage.Should().Be("Showing Cache.Minutes in every run.");
    }

    [Fact]
    public void Marks_filter_and_survive_a_refresh()
    {
        var (viewModel, curation) = Load();
        var cache = Setting(viewModel, "Cache.Minutes");

        viewModel.ToggleMark(cache);
        viewModel.MarkFilter = CompareMarkFilter.Marked;
        viewModel.VisibleFiles.Should().ContainSingle().Which.VisibleRows.Select(row => row.Key).Should().Equal("Cache.Minutes");

        curation.Update(document => document with { Groups = [new CurationGroup { Name = "x" }] });
        viewModel.VisibleFiles.Single().VisibleRows.Single().IsMarked.Should().BeTrue("marks are kept when curation is re-applied");

        viewModel.MarkFilter = CompareMarkFilter.Unmarked;
        viewModel.VisibleFiles[0].VisibleRows.Select(row => row.Key).Should().Equal("db.password");

        viewModel.ClearMarks();
        viewModel.MarkFilter = CompareMarkFilter.Marked;
        viewModel.VisibleFiles.Should().BeEmpty();
        viewModel.EmptyMessage.Should().StartWith("Nothing is marked");

        viewModel.MarkFilter = CompareMarkFilter.All;
        viewModel.ToggleMark(new CompareContext(viewModel.VisibleFiles[0]));
        viewModel.VisibleFiles[0].AllRows.Should().OnlyContain(row => row.IsMarked, "marking a file marks every setting in it");
    }

    [Fact]
    public void Groups_are_added_viewed_and_removed()
    {
        var (viewModel, curation) = Load();

        viewModel.AddToGroup(Setting(viewModel, "Cache.Minutes"), "Caching");
        viewModel.AddToGroup(Setting(viewModel, "Logging.Default"), "caching");
        viewModel.AddToGroup(Setting(viewModel, "Cache.Minutes"), "  ");

        curation.Document.Groups.Should().ContainSingle().Which.Members.Should().HaveCount(2);
        viewModel.GroupNames.Should().Equal("Caching");
        viewModel.GroupsOf(Setting(viewModel, "Cache.Minutes")).Should().Equal("Caching");

        viewModel.ViewGroup("Caching");
        viewModel.DifferencesOnly.Should().BeFalse();
        viewModel.GroupFilterText.Should().Be("Showing group \"Caching\"");
        viewModel.VisibleFiles.Single().VisibleRows.Select(row => row.Key).Should().Equal("Cache.Minutes", "Logging.Default");

        viewModel.RemoveFromGroup(Setting(viewModel, "Logging.Default"), "Caching");
        viewModel.VisibleFiles.Single().VisibleRows.Select(row => row.Key).Should().Equal("Cache.Minutes");

        curation.Update(document => document with { Rules = [new CurationRule { Name = "all", KeyPattern = "*", Groups = ["Everything"] }] });
        viewModel.RemoveFromGroup(Setting(viewModel, "Cache.Minutes"), "Everything");
        viewModel.StatusMessage.Should().Contain("through a pattern or rule");

        viewModel.SaveGroups([]);
        viewModel.HasGroupFilter.Should().BeFalse("the viewed group no longer exists");
    }

    [Fact]
    public void Rules_are_drafted_widened_and_label_files()
    {
        var (viewModel, curation) = Load();
        var cache = Setting(viewModel, "Cache.Minutes");

        var draft = viewModel.RuleDraftFor(cache);
        draft.Should().BeEquivalentTo(new CurationRule { Name = "appsettings.json Cache.Minutes", FilePattern = "inetpub/app/appsettings.json", KeyPattern = "Cache.Minutes" });
        viewModel.RuleDraftFor(new CompareContext(cache.File)).KeyPattern.Should().BeEmpty();

        viewModel.SaveRules([draft with { Name = "cache", AppName = "Contoso", FileLabel = "App settings" }, new CurationRule { Name = "files", FilePattern = "a.ini" }]);
        viewModel.AddToRule(Setting(viewModel, "db.password"), "cache");
        viewModel.AddToRule(new CompareContext(viewModel.VisibleFiles[1]), "files");

        curation.Document.Rules[0].KeyPattern.Should().Be("Cache.Minutes;db.password");
        curation.Document.Rules[1].FilePattern.Should().Be("a.ini;legacy.ini");
        viewModel.RuleNames.Should().Equal("cache", "files");
        viewModel.VisibleFiles[0].FileName.Should().Be("App settings");
        viewModel.VisibleFiles[0].AppName.Should().Be("Contoso");
    }

    [Fact]
    public async Task The_review_list_persists_filters_and_exports()
    {
        var (viewModel, curation) = Load();
        var cache = Setting(viewModel, "Cache.Minutes");

        viewModel.ToggleReview(cache);
        viewModel.ReviewToggleText.Should().Be("Review list (1)");
        viewModel.IsInReview(Setting(viewModel, "Cache.Minutes")).Should().BeTrue();
        viewModel.ReviewOnly = true;
        viewModel.VisibleFiles.Single().VisibleRows.Select(row => row.Key).Should().Equal("Cache.Minutes");

        await viewModel.ExportReviewCommand.ExecuteAsync(null);
        viewModel.StatusMessage.Should().StartWith("Saved ").And.Contain("settings-review-");

        viewModel.ToggleReview(Setting(viewModel, "Cache.Minutes"));
        curation.Document.Review.Should().BeEmpty();
        viewModel.EmptyMessage.Should().StartWith("The review list is empty");
    }

    [Fact]
    public void History_asks_for_the_setting_and_the_value()
    {
        var (viewModel, curation) = Load();
        var at = DateTimeOffset.UtcNow;
        curation.Entries.Add(new HistoryEntry(1, at, "prod", "inetpub/app/appsettings.json", "Cache.Minutes", "60", false, CurationTarget.ValueHashOf("60")));
        curation.Entries.Add(new HistoryEntry(1, at, "prod", "other.json", "Timeout", "60", false, CurationTarget.ValueHashOf("60")));

        var both = viewModel.History(Setting(viewModel, "Cache.Minutes", 2), CompareHistoryKind.Both);
        both.SettingOverTime.Should().ContainSingle();
        both.SettingElsewhere.Should().ContainSingle();
        both.ValueElsewhere.Select(entry => entry.Key).Should().Equal("Cache.Minutes", "Timeout");

        viewModel.History(Setting(viewModel, "Cache.Minutes", 2), CompareHistoryKind.Value).SettingOverTime.Should().BeEmpty();
        viewModel.History(new CompareContext(viewModel.VisibleFiles[0]), CompareHistoryKind.Setting).IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void Raw_text_comes_from_the_owner_and_questions_are_placeholders()
    {
        var (viewModel, _) = Load();
        var cache = Setting(viewModel, "Cache.Minutes", 2);

        viewModel.RawText(cache, "c").Should().BeNull("no provider yet");
        viewModel.RawTextProvider = (configId, hostId) => $"{configId}@{hostId}";
        viewModel.RawText(cache, "c").Should().Be("json/app@c");

        CompareViewModel.WhatIs(cache, WhatIsSubject.Value).Should().Be("What does the value 60 of Cache.Minutes mean?");
        CompareViewModel.WhatIs(cache, WhatIsSubject.Setting).Should().Contain("Cache.Minutes");
        CompareViewModel.WhatIs(cache, WhatIsSubject.Source).Should().Contain("inetpub/app/appsettings.json");
        CompareViewModel.WhatIs(cache, WhatIsSubject.Application).Should().StartWith("Which application");
        viewModel.TreeOf(cache.File).Select(node => node.Name).Should().Equal("Cache", "Logging", "db");
    }

    [Fact]
    public void A_file_from_the_catalog_is_found_and_shown_even_when_filtered_out()
    {
        var (viewModel, _) = Load();
        viewModel.FileFor("JSON/SAME").Should().NotBeNull("config ids match case-insensitively");
        viewModel.FileFor("nope").Should().BeNull();

        viewModel.SearchText = "legacy";
        viewModel.ReviewOnly = true;
        var same = viewModel.FileFor("json/same")!;
        viewModel.VisibleFiles.Should().NotContain(same);

        viewModel.ShowFile(same);

        viewModel.SelectedFile.Should().BeSameAs(same);
        viewModel.DifferencesOnly.Should().BeFalse("the file does not differ, so differences-only had to go");
        viewModel.ReviewOnly.Should().BeFalse();
        viewModel.SearchText.Should().BeEmpty();

        var app = viewModel.FileFor("json/app")!;
        viewModel.ShowFile(app);
        viewModel.SelectedFile.Should().BeSameAs(app);
    }
}
