using DriftBuster.Backend.Models;
using DriftBuster.Gui.Tests.Fakes;
using DriftBuster.Gui.ViewModels;

namespace DriftBuster.Gui.Tests.ViewModels;

/// <summary>Hunt findings as a list beside the selected one: rule chips, file and text filters, copies and false-positive reports.</summary>
public sealed class HuntFindingsTests : IDisposable
{
    private readonly DirectoryInfo _tmp = Directory.CreateTempSubdirectory("driftbuster-hunt-");

    public void Dispose() => _tmp.Delete(recursive: true);

    private static HuntHit Hit(string rule, string file, int line, string excerpt, string? token = null) => new()
    {
        Rule = new HuntRuleSummary { Name = rule, Description = $"{rule} values", TokenName = token },
        RelativePath = file,
        Path = "/root/" + file,
        LineNumber = line,
        Excerpt = excerpt,
    };

    private async Task<HuntViewModel> ScannedAsync()
    {
        var service = new FakeDriftbusterService
        {
            HuntResponse = new HuntResult
            {
                Count = 4,
                Hits =
                [
                    Hit("server-name", "web/app.config", 3, "server=db01", "server_name"),
                    Hit("server-name", "web/other.config", 9, "host=db02"),
                    Hit("install-path", "web/app.config", 5, "path=C:\\Program Files\\App"),
                    Hit("server-name", "svc/svc.ini", 1, "server=db03\tbackup"),
                ],
                RawJson = "{}",
            },
        };
        var viewModel = new HuntViewModel(service) { DirectoryPath = _tmp.FullName };
        await viewModel.RunHuntCommand.ExecuteAsync(null).ConfigureAwait(true);
        return viewModel;
    }

    [Fact]
    public async Task Findings_come_with_rule_chips_and_the_first_selected()
    {
        var viewModel = await ScannedAsync();

        viewModel.RuleChips.Select(chip => (chip.Name, chip.Count)).Should().Equal(("server-name", 3), ("install-path", 1));
        viewModel.VisibleHits.Should().HaveCount(4);
        viewModel.SelectedHit.Should().BeSameAs(viewModel.Hits[0]);
        viewModel.HasSelectedHit.Should().BeTrue();
        viewModel.HasFilters.Should().BeFalse();
        viewModel.FilterText.Should().BeEmpty();
    }

    [Fact]
    public async Task Rule_file_and_text_filters_narrow_the_list()
    {
        var viewModel = await ScannedAsync();

        viewModel.ToggleRuleCommand.Execute(viewModel.RuleChips[0]);
        viewModel.RuleChips[0].IsSelected.Should().BeTrue();
        viewModel.VisibleHits.Should().HaveCount(3);

        viewModel.FileFilter = "WEB/app.config";
        viewModel.VisibleHits.Select(hit => hit.Location).Should().Equal("web/app.config:3");

        viewModel.FileFilter = null;
        viewModel.SearchText = "db0";
        viewModel.VisibleHits.Should().HaveCount(3);
        viewModel.SearchText = "backup";
        viewModel.VisibleHits.Should().ContainSingle().Which.RelativePath.Should().Be("svc/svc.ini");
        viewModel.FilterText.Should().Be("Showing 1 of 4: rule server-name, \"backup\"");

        viewModel.ToggleRuleCommand.Execute(viewModel.RuleChips[0]);
        viewModel.RuleFilter.Should().BeNull("a second click on the chip clears it");
        viewModel.ClearFiltersCommand.Execute(null);
        viewModel.VisibleHits.Should().HaveCount(4);
        viewModel.HasFilters.Should().BeFalse();
    }

    [Fact]
    public async Task Findings_copy_as_json_and_tsv_and_report_as_false_positives()
    {
        var viewModel = await ScannedAsync();
        var hit = viewModel.Hits[3];

        hit.ToJson().Should().Contain("\"rule\": \"server-name\"").And.Contain("\"line\": 1");
        hit.ToTsv().Should().Be("Rule\tToken\tFile\tLine\tExcerpt" + Environment.NewLine + "server-name\t\tsvc/svc.ini\t1\tserver=db03 backup");
        viewModel.Hits[2].ToJson().Should().Contain("C:\\\\Program Files\\\\App");

        var report = hit.BugReport();
        report.Category.Should().Be("Hunt finding is not a real secret or value (false positive)");
        report.Payload.Should().Contain("\"source_path\": \"svc/svc.ini\"").And.Contain("\"mode\": \"hunt\"").And.Contain("\"line\": \"1\"");
        new BugReportDraft("a", "b", "c", "d", new Dictionary<string, string>(StringComparer.Ordinal), new Dictionary<string, string>(StringComparer.Ordinal), "no such category").Category
            .Should().Be(BugReportDraft.Categories[0]);
    }

    [Fact]
    public async Task A_new_scan_drops_filters_that_no_longer_apply()
    {
        var viewModel = await ScannedAsync();
        viewModel.RuleFilter = "gone-rule";
        viewModel.FileFilter = "gone/file";

        await viewModel.RunHuntCommand.ExecuteAsync(null);

        viewModel.RuleFilter.Should().BeNull();
        viewModel.FileFilter.Should().BeNull();
        viewModel.VisibleHits.Should().HaveCount(4);
    }
}
