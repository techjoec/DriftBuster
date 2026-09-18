using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using AwesomeAssertions;
using DriftBuster.Backend.Models;
using DriftBuster.Gui.ViewModels;
using Xunit;

namespace DriftBuster.Gui.Tests.ViewModels;

public sealed class ConfigDrilldownViewModelTests
{
    private static ConfigDrilldown BuildSample()
    {
        return new ConfigDrilldown
        {
            ConfigId = "appsettings",
            DisplayName = "appsettings.json",
            Format = "json",
            DiffBefore = "{\n  \"Logging\": \"Information\"\n}",
            DiffAfter = "{\n  \"Logging\": \"Warning\"\n}",
            UnifiedDiff = string.Empty,
            DriftCount = 2,
            BaselineHostId = "server01",
            LastUpdated = DateTimeOffset.UtcNow,
            HasMaskedTokens = true,
            HasValidationIssues = true,
            HasSecrets = true,
            Notes = new[] { "Investigate logging level" },
            Servers = new[]
            {
                new ConfigServerDetail
                {
                    HostId = "server01",
                    Label = "Baseline",
                    Present = true,
                    IsBaseline = true,
                    Status = "Baseline",
                    DriftLineCount = 0,
                    RedactionStatus = "Visible",
                    Masked = false,
                    HasSecrets = false,
                    LastSeen = DateTimeOffset.UtcNow.AddMinutes(-10),
                },
                new ConfigServerDetail
                {
                    HostId = "server02",
                    Label = "Drifting",
                    Present = true,
                    IsBaseline = false,
                    Status = "Drift",
                    DriftLineCount = 4,
                    RedactionStatus = "Masked",
                    Masked = true,
                    HasSecrets = true,
                    LastSeen = DateTimeOffset.UtcNow.AddMinutes(-5),
                },
            },
        };
    }

    [Fact]
    public async Task Commands_emit_events_and_toggle_modes()
    {
        var drilldown = new ConfigDrilldownViewModel(BuildSample());

        drilldown.IsSideBySide.Should().BeTrue();
        drilldown.ToggleModeCommand.Execute(DiffViewMode.Unified);
        drilldown.IsUnified.Should().BeTrue();

        drilldown.SelectNoneCommand.Execute(null);
        drilldown.Servers.All(server => !server.IsSelected).Should().BeTrue();
        drilldown.ReScanSelectedCommand.CanExecute(null).Should().BeFalse();

        drilldown.SelectAllCommand.Execute(null);
        drilldown.ReScanSelectedCommand.CanExecute(null).Should().BeTrue();
        drilldown.BaselineLabel.Should().Be("Baseline");
        drilldown.BaselineHostSummary.Should().Contain("server01");

        string? exportedFormat = null;
        string? exportPayload = null;
        drilldown.ExportRequested += (_, e) =>
        {
            exportedFormat = e.Value.Format.ToString();
            exportPayload = e.Value.Payload;
        };

        await drilldown.ExportHtmlCommand.ExecuteAsync(null);
        exportedFormat.Should().Be("Html");
        exportPayload.Should().NotBeNull();
        exportPayload!.Should().Contain("Logging");

        string? copiedJson = null;
        drilldown.CopyJsonRequested += (_, e) => copiedJson = e.Value;
        await drilldown.CopyJsonCommand.ExecuteAsync(null);
        copiedJson.Should().NotBeNullOrEmpty();

        string[]? rescannedHosts = null;
        drilldown.ReScanRequested += (_, e) => rescannedHosts = e.Value.ToArray();
        drilldown.ReScanSelectedCommand.Execute(null);
        rescannedHosts.Should().Contain("Drifting");

        var backInvoked = false;
        drilldown.BackRequested += (_, _) => backInvoked = true;
        drilldown.BackCommand.Execute(null);
        backInvoked.Should().BeTrue();
    }

    [Fact]
    public async Task Json_export_contains_metadata()
    {
        var drilldown = new ConfigDrilldownViewModel(BuildSample());
        string? payload = null;
        drilldown.ExportRequested += (_, e) => payload = e.Value.Payload;
        await drilldown.ExportJsonCommand.ExecuteAsync(null);

        payload.Should().NotBeNull();
        using var json = JsonDocument.Parse(payload!);
        json.RootElement.TryGetProperty("ConfigId", out var configId).Should().BeTrue();
        configId.GetString().Should().Be("appsettings");
        json.RootElement.TryGetProperty("Servers", out var servers).Should().BeTrue();
        servers.GetArrayLength().Should().Be(2);
    }

    [Fact]
    public void Shows_the_diff_against_any_server_and_names_both_sides()
    {
        var source = BuildSample();
        source.DiffHostId = "server02";
        source.HostDiffs =
        [
            new ConfigHostDiff { HostId = "server02", After = source.DiffAfter, UnifiedDiff = string.Empty },
            new ConfigHostDiff { HostId = "server03", After = "{\n  \"Logging\": \"Information\"\n}", UnifiedDiff = string.Empty },
        ];
        using var drilldown = new ConfigDrilldownViewModel(source);

        drilldown.HasComparisonChoice.Should().BeTrue();
        drilldown.SelectedComparison!.Label.Should().Be("Drifting");
        drilldown.Comparisons[1].Label.Should().Be("server03", "a host without a server entry is named by its id");
        drilldown.Lines.LeftTitle.Should().Be("Baseline");
        drilldown.Lines.RightTitle.Should().Be("Drifting");
        drilldown.Lines.ChangeCount.Should().Be(1);

        drilldown.SelectedComparison = drilldown.Comparisons[1];
        drilldown.Lines.ChangeCount.Should().Be(0);
        drilldown.Lines.SummaryText.Should().Be("No changes.");

        drilldown.DiffMode = DiffViewMode.Unified;
        drilldown.Lines.IsUnified.Should().BeTrue();
        drilldown.HasNotes.Should().BeTrue();
    }

    [Fact]
    public void Without_per_server_copies_the_one_diff_is_shown()
    {
        using var drilldown = new ConfigDrilldownViewModel(BuildSample());

        drilldown.HasComparisonChoice.Should().BeFalse();
        drilldown.Lines.RightTitle.Should().Be("Comparison");
        drilldown.Lines.ChangeCount.Should().Be(1);
    }
}
