using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using DriftBuster.Backend.Models;
using DriftBuster.Gui.ViewModels;
using FluentAssertions;
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
        drilldown.ExportRequested += (_, request) =>
        {
            exportedFormat = request.Format.ToString();
            exportPayload = request.Payload;
        };

        await drilldown.ExportHtmlCommand.ExecuteAsync(null);
        exportedFormat.Should().Be("Html");
        exportPayload.Should().NotBeNull();
        exportPayload!.Should().Contain("Logging");

        string? copiedJson = null;
        drilldown.CopyJsonRequested += (_, payload) => copiedJson = payload;
        await drilldown.CopyJsonCommand.ExecuteAsync(null);
        copiedJson.Should().NotBeNullOrEmpty();

        string[]? rescannedHosts = null;
        drilldown.ReScanRequested += (_, hosts) => rescannedHosts = hosts.ToArray();
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
        drilldown.ExportRequested += (_, request) => payload = request.Payload;
        await drilldown.ExportJsonCommand.ExecuteAsync(null);

        payload.Should().NotBeNull();
        using var json = JsonDocument.Parse(payload!);
        json.RootElement.TryGetProperty("ConfigId", out var configId).Should().BeTrue();
        configId.GetString().Should().Be("appsettings");
        json.RootElement.TryGetProperty("Servers", out var servers).Should().BeTrue();
        servers.GetArrayLength().Should().Be(2);
    }
}
