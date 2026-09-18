using DriftBuster.Gui.Tests.Fakes;
using DriftBuster.Gui.ViewModels;

namespace DriftBuster.Gui.Tests.ViewModels;

/// <summary>The Compare view's plain summary, its filters and the report it saves.</summary>
public sealed class CompareViewModelTests
{
    [Fact]
    public void Loads_a_headline_server_lines_and_only_differing_files()
    {
        var viewModel = new CompareViewModel();

        viewModel.Load(SampleComparison.Build());

        viewModel.Headline.Should().Be("1 of 2 servers differ from baseline.");
        viewModel.Servers.Select(server => server.Summary).Should().Equal(
            "baseline (the others are compared with it)",
            "matches the baseline",
            "2 settings differ in 1 file, 1 file missing");
        viewModel.Columns.Should().Equal("baseline", "staging", "prod");
        viewModel.VisibleFiles.Select(file => file.Path).Should().Equal("inetpub/app/appsettings.json", "legacy.ini");
        var app = viewModel.VisibleFiles[0];
        app.Summary.Should().Be("2 settings differ");
        app.VisibleRows.Select(row => row.Key).Should().Equal("Cache.Minutes", "db.password");
        app.VisibleRows[1].Cells.Select(cell => cell.Text).Should().Equal("•••• (same)", "•••• (same)", "•••• (differs)");
        viewModel.VisibleFiles[1].Summary.Should().Be("missing on prod");
    }

    [Fact]
    public void Filters_by_differences_search_and_server()
    {
        var viewModel = new CompareViewModel();
        viewModel.Load(SampleComparison.Build());

        viewModel.DifferencesOnly = false;
        viewModel.VisibleFiles.Should().HaveCount(3);
        viewModel.VisibleFiles[0].VisibleRows.Should().HaveCount(3);

        viewModel.SearchText = "logging";
        viewModel.VisibleFiles.Should().ContainSingle().Which.VisibleRows.Select(row => row.Key).Should().Equal("Logging.Default");

        viewModel.SearchText = "nothing-like-this";
        viewModel.EmptyMessage.Should().Be("Nothing matches \"nothing-like-this\".");

        viewModel.SearchText = string.Empty;
        viewModel.DifferencesOnly = true;
        viewModel.FocusServerCommand.Execute(viewModel.Servers[1]);
        viewModel.FocusText.Should().Be("Showing only what differs on staging");
        viewModel.Servers[1].IsFocused.Should().BeTrue();
        viewModel.EmptyMessage.Should().Be("No differences: every server matches the baseline.");

        viewModel.FocusServerCommand.Execute(viewModel.Servers[0]);
        viewModel.HasFocus.Should().BeTrue("the baseline cannot be focused");
        viewModel.ClearFocusCommand.Execute(null);
        viewModel.HasFocus.Should().BeFalse();
        viewModel.VisibleFiles.Should().HaveCount(2);
    }

    [Fact]
    public async Task Details_are_requested_for_a_file_and_the_report_is_saved()
    {
        var viewModel = new CompareViewModel();
        viewModel.Load(SampleComparison.Build());
        string? requested = null;
        viewModel.DetailsRequested += (_, e) => requested = e.Value;

        viewModel.OpenDetailsCommand.Execute(viewModel.VisibleFiles[0]);
        requested.Should().Be("json/app");

        await viewModel.SaveReportCommand.ExecuteAsync(null);
        viewModel.StatusMessage.Should().StartWith("Saved ");
        var html = viewModel.StatusMessage["Saved ".Length..viewModel.StatusMessage.IndexOf(" and ", StringComparison.Ordinal)];
        File.ReadAllText(html).Should().Contain("<td class=\"d\">60</td>").And.NotContain("password\">");
        File.ReadAllText(Path.ChangeExtension(html, ".csv")).Should().Contain("legacy.ini,(file),present,present,file missing");

        viewModel.Reset();
        viewModel.HasData.Should().BeFalse();
        viewModel.EmptyMessage.Should().Be("Run a scan to compare servers.");
    }
}
