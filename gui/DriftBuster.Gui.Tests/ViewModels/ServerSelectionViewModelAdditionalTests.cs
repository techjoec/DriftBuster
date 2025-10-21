using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DriftBuster.Backend.Models;
using DriftBuster.Gui.Services;
using DriftBuster.Gui.Tests.Fakes;
using DriftBuster.Gui.ViewModels;
using FluentAssertions;
using Xunit;

namespace DriftBuster.Gui.Tests.ViewModels;

public sealed class ServerSelectionViewModelAdditionalTests
{
    private static ServerScanResponse BuildResponse(string baselineId, IReadOnlyList<ServerScanPlan> plans)
    {
        var labels = plans.Select(plan => string.IsNullOrWhiteSpace(plan.Label) ? plan.HostId : plan.Label).ToArray();
        return new ServerScanResponse
        {
            Results = plans.Select(plan => new ServerScanResult
            {
                HostId = plan.HostId,
                Label = plan.Label,
                Status = ServerScanStatus.Succeeded,
                Message = "Completed",
                Timestamp = DateTimeOffset.UtcNow,
                Availability = ServerAvailabilityStatus.Found,
            }).ToArray(),
            Catalog = new[]
            {
                new ConfigCatalogEntry
                {
                    ConfigId = "plugins",
                    DisplayName = "plugins.conf",
                    Format = "ini",
                    DriftCount = 1,
                    Severity = "medium",
                    PresentHosts = labels.Take(labels.Length - 1).ToArray(),
                    MissingHosts = labels.Length > 1 ? new[] { labels[^1] } : Array.Empty<string>(),
                    CoverageStatus = labels.Length > 1 ? "partial" : "full",
                    HasMaskedTokens = true,
                    LastUpdated = DateTimeOffset.UtcNow,
                },
            },
            Drilldown = new[]
            {
                new ConfigDrilldown
                {
                    ConfigId = "plugins",
                    DisplayName = "plugins.conf",
                    Format = "ini",
                    DriftCount = 1,
                    BaselineHostId = baselineId,
                    DiffBefore = "before",
                    DiffAfter = "after",
                    UnifiedDiff = string.Empty,
                    Notes = new[] { "Investigate drift" },
                    Servers = plans.Select((plan, index) => new ConfigServerDetail
                    {
                        HostId = plan.HostId,
                        Label = plan.Label,
                        Present = true,
                        IsBaseline = index == 0,
                        Status = index == 0 ? "Baseline" : "Drift",
                        DriftLineCount = index,
                        RedactionStatus = index % 2 == 0 ? "Masked" : "Visible",
                        Masked = index % 2 == 0,
                        HasSecrets = index % 2 == 1,
                        LastSeen = DateTimeOffset.UtcNow,
                    }).ToArray(),
                },
            },
        };
    }

    [Fact]
    public async Task Handles_root_management_and_session_persistence()
    {
        var cache = new InMemorySessionCacheService();

        var service = new FakeDriftbusterService
        {
            RunServerScansHandler = (plans, progress, token) =>
            {
                var list = plans.ToList();
                foreach (var plan in list)
                {
                    progress?.Report(new ScanProgress
                    {
                        HostId = plan.HostId,
                        Status = ServerScanStatus.Running,
                        Message = "Scanning",
                        Timestamp = DateTimeOffset.UtcNow,
                    });
                }

                return Task.FromResult(BuildResponse(list[0].HostId, list));
            },
        };

        var toast = new ToastService(action => action());
        var viewModel = new ServerSelectionViewModel(service, toast, cache);

        var server = viewModel.Servers[0];
        server.Scope = ServerScanScope.CustomRoots;
        var absoluteRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(absoluteRoot);
        server.NewRootPath = absoluteRoot;
        viewModel.AddRootCommand.Execute(server);
        server.Roots.Should().NotBeEmpty();

        server.NewRootPath = "relative";
        viewModel.AddRootCommand.Execute(server);
        var lastRoot = server.Roots.Last();
        lastRoot.ValidationState.Should().Be(RootValidationState.Invalid);
        lastRoot.StatusMessage.Should().Contain("absolute");

        await viewModel.RunAllCommand.ExecuteAsync(null);
        viewModel.CatalogViewModel.HasEntries.Should().BeTrue();
        viewModel.FilteredActivityEntries.Should().NotBeEmpty();

        viewModel.PersistSessionState = true;
        await viewModel.SaveSessionCommand.ExecuteAsync(null);
        cache.Snapshot.Should().NotBeNull();

        viewModel.ActivityFilter = ActivityFilterOption.Errors;
        viewModel.ActivityFilter = ActivityFilterOption.All;

        viewModel.ClearHistoryCommand.Execute(null);
        cache.Cleared.Should().BeTrue();
        viewModel.HasActiveServers.Should().BeTrue();
    }
}
