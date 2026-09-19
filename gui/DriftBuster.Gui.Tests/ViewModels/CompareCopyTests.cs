using DriftBuster.Gui.Tests.Fakes;
using DriftBuster.Gui.ViewModels;

namespace DriftBuster.Gui.Tests.ViewModels;

/// <summary>What Copy puts on the clipboard, and what a bug report holds and links to.</summary>
public sealed class CompareCopyTests
{
    private static CompareContext Context(string key, int cell = -1)
    {
        var viewModel = new CompareViewModel(new InMemoryCurationService());
        viewModel.Load(SampleComparison.Build());
        var file = viewModel.VisibleFiles[0];
        var row = file.AllRows.Single(candidate => string.Equals(candidate.Key, key, StringComparison.Ordinal));
        return new CompareContext(file, row, cell >= 0 ? row.Cells[cell] : null);
    }

    [Fact]
    public void Copies_in_every_format_and_scope()
    {
        var cache = Context("Cache.Minutes", 2);
        var newline = Environment.NewLine;

        CompareViewModel.Copy(cache, CompareCopyFormat.Text, CompareCopyScope.Setting).Should().Be("Cache.Minutes");
        CompareViewModel.Copy(cache, CompareCopyFormat.Text, CompareCopyScope.ThisValue).Should().Be("60");
        CompareViewModel.Copy(cache, CompareCopyFormat.Text, CompareCopyScope.Values).Should().Be($"baseline: 15{newline}staging: 15{newline}prod: 60");
        CompareViewModel.Copy(cache, CompareCopyFormat.Text, CompareCopyScope.All).Should().StartWith("inetpub/app/appsettings.json Cache.Minutes");
        CompareViewModel.Copy(cache, CompareCopyFormat.Tsv, CompareCopyScope.All).Should().Be($"File\tSetting\tbaseline\tstaging\tprod{newline}inetpub/app/appsettings.json\tCache.Minutes\t15\t15\t60");
        CompareViewModel.Copy(cache, CompareCopyFormat.Tsv, CompareCopyScope.ThisValue).Should().Be($"prod{newline}60");
        CompareViewModel.Copy(cache, CompareCopyFormat.Tsv, CompareCopyScope.Setting).Should().Be("Cache.Minutes");
        CompareViewModel.Copy(cache, CompareCopyFormat.Json, CompareCopyScope.ThisValue).Should().Contain("\"prod\": \"60\"");
        CompareViewModel.Copy(cache, CompareCopyFormat.Json, CompareCopyScope.Setting).Should().Contain("\"setting\": \"Cache.Minutes\"");
        CompareViewModel.Copy(cache, CompareCopyFormat.Json, CompareCopyScope.All).Should().Contain("\"file\"").And.Contain("\"values\"");
        CompareViewModel.Copy(cache, CompareCopyFormat.Hex, CompareCopyScope.ThisValue).Should().Be("36 30");
        CompareViewModel.Copy(new CompareContext(cache.File), CompareCopyFormat.Text, CompareCopyScope.All).Should().Be("inetpub/app/appsettings.json");
        CompareViewModel.Copy(new CompareContext(cache.File), CompareCopyFormat.Hex, CompareCopyScope.All).Should().StartWith("69 6E");
    }

    [Fact]
    public void Masked_values_are_copied_as_their_marker()
    {
        var password = Context("db.password");

        CompareViewModel.Copy(password, CompareCopyFormat.Text, CompareCopyScope.Values).Should().NotContain("two").And.Contain("•••• (differs)");
    }

    [Fact]
    public void A_bug_report_holds_the_setting_and_links_to_a_prefilled_issue()
    {
        var draft = CompareViewModel.BugReport(Context("db.password", 2));
        draft.Description = "Wrong \"type\" & value";
        draft.ContactEmail = "someone@example.invalid";
        draft.Category = BugReportDraft.Categories[2];

        draft.Title.Should().Be("Wrong value type: inetpub/app/appsettings.json db.password");
        draft.Payload.Should().Contain("\"source_path\": \"inetpub/app/appsettings.json\"").And.Contain("\"value_masked\": \"yes\"").And.NotContain("\"two\"");
        draft.Payload.Should().Contain("\"format\": \"json\"").And.Contain("\"mode\": \"settings\"");
        draft.PayloadText.Should().Be(draft.Payload, "every field change rebuilds the text that is sent");
        draft.IncludeValues = false;
        draft.PayloadText.Should().NotContain("\"baseline\":");
        draft.PayloadText = "{\"edited\": true}";
        draft.GitHubIssueUrl().Should().Contain(Uri.EscapeDataString("{\"edited\": true}"), "the link carries the edited text");
        var url = draft.GitHubIssueUrl();
        url.Should().StartWith(BugReportDraft.IssuesUrl + "?title=Wrong%20value%20type");
        url.Should().Contain("labels=bug").And.Contain(Uri.EscapeDataString("Wrong \"type\" & value"));
        new BugReportDraft(new CompareContext(Context("Cache.Minutes").File)).Title.Should().Be("Wrong detection (format or type): inetpub/app/appsettings.json");
    }

    [Fact]
    public void A_long_bug_report_is_cut_to_fit_a_link()
    {
        var draft = CompareViewModel.BugReport(Context("Cache.Minutes"));
        draft.Description = new string('x', 20000);

        var url = draft.GitHubIssueUrl();

        url.Length.Should().BeLessThanOrEqualTo(BugReportDraft.MaxUrlLength);
        url.Should().Contain(Uri.EscapeDataString("(The payload was cut to fit in a link"));
    }
}
