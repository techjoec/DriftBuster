using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using DriftBuster.Backend.Models;
using DriftBuster.Gui.Services;
using DriftBuster.Gui.Tests.Fakes;
using DriftBuster.Gui.ViewModels;

using FluentAssertions;

using Xunit;

namespace DriftBuster.Gui.Tests.Ui;

[Collection(HeadlessCollection.Name)]
public sealed class ServerSelectionViewTests
{
    [Fact]
    public void ShouldInitialiseWithDefaultHosts()
    {
        var viewModel = CreateViewModel();

        viewModel.Servers.Should().HaveCount(6);
        viewModel.Servers.Take(3).All(server => server.IsEnabled).Should().BeTrue();
        viewModel.Servers[0].Label.Should().Be("App Inc");
    }

    [Fact]
    public void ShouldValidateCustomRoots()
    {
        var viewModel = CreateViewModel();
        var server = viewModel.Servers[0];

        server.Scope = ServerScanScope.CustomRoots;
        server.NewRootPath = "relative/path";
        viewModel.AddRootCommand.Execute(server);

        server.Roots.Should().NotBeEmpty();
        server.Roots.Last().ValidationState.Should().Be(RootValidationState.Invalid);
        server.Roots.Last().StatusMessage.Should().Contain("absolute");
    }

    [Fact]
    public async Task ShouldRunScanAndUpdateStatuses()
    {
        var fakeService = new FakeDriftbusterService
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
                        Message = "Running",
                        Timestamp = DateTimeOffset.UtcNow,
                    });
                }

                var labels = list.Select(plan => string.IsNullOrWhiteSpace(plan.Label) ? plan.HostId : plan.Label).ToArray();

                var results = list.Select(plan => new ServerScanResult
                {
                    HostId = plan.HostId,
                    Label = plan.Label,
                    Status = ServerScanStatus.Succeeded,
                    Message = "Completed",
                    Timestamp = DateTimeOffset.UtcNow,
                }).ToArray();

                var catalog = new[]
                {
                    new ConfigCatalogEntry
                    {
                        ConfigId = "appsettings.json",
                        DisplayName = "appsettings.json",
                        Format = "json",
                        DriftCount = 1,
                        Severity = "medium",
                        PresentHosts = labels,
                        MissingHosts = Array.Empty<string>(),
                        LastUpdated = DateTimeOffset.UtcNow,
                        CoverageStatus = "full",
                    },
                };

                return Task.FromResult(new ServerScanResponse { Results = results, Catalog = catalog });
            },
        };

        var cache = new InMemorySessionCacheService();
        var viewModel = CreateViewModel(fakeService, cache);

        await viewModel.RunAllCommand.ExecuteAsync(null);

        SpinWait.SpinUntil(() => viewModel.Servers[0].RunState == ServerScanStatus.Succeeded, TimeSpan.FromMilliseconds(200)).Should().BeTrue();
        viewModel.Servers[0].StatusText.Should().Be("Completed");
        viewModel.CatalogViewModel.HasEntries.Should().BeTrue();
        viewModel.IsViewingCatalog.Should().BeTrue();
    }

    [Fact]
    public async Task ShouldPersistSessionWhenUserRequestsSave()
    {
        var cache = new InMemorySessionCacheService();
        var viewModel = CreateViewModel(cache: cache);

        viewModel.PersistSessionState = true;
        viewModel.Servers[0].Label = "Primary";

        await viewModel.SaveSessionCommand.ExecuteAsync(null);

        cache.Snapshot.Should().NotBeNull();
        cache.Snapshot!.Servers.Should().Contain(entry => entry.Label == "Primary");
    }

    [Fact]
    public void ClearingHistoryShouldClearCacheWhenPersistenceEnabled()
    {
        var cache = new InMemorySessionCacheService
        {
            Snapshot = new ServerSelectionCache { PersistSession = true },
        };

        var viewModel = CreateViewModel(cache: cache);
        viewModel.PersistSessionState = true;

        viewModel.ClearHistoryCommand.Execute(null);

        cache.Cleared.Should().BeTrue();
        viewModel.IsViewingCatalog.Should().BeFalse();
        viewModel.CatalogViewModel.HasEntries.Should().BeFalse();
    }

    [Fact]
    public async Task ShouldFilterCatalogEntries()
    {
        var viewModel = CreateViewModel();
        await viewModel.RunAllCommand.ExecuteAsync(null);

        viewModel.CatalogViewModel.SelectedFormat = "json";
        viewModel.CatalogViewModel.SearchText = "appsettings";

        viewModel.CatalogViewModel.FilteredEntries.Count.Should().BeGreaterThan(0);
        viewModel.CatalogViewModel.FilteredEntries
            .Should().OnlyContain(entry => entry.DisplayName.Contains("appsettings", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ShouldReScanMissingHostsFromCatalog()
    {
        var firstBatch = new TaskCompletionSource<IReadOnlyList<ServerScanPlan>>();
        var secondBatch = new TaskCompletionSource<IReadOnlyList<ServerScanPlan>>();
        var invocation = 0;

        var fakeService = new FakeDriftbusterService
        {
            RunServerScansHandler = (plans, progress, token) =>
            {
                invocation++;
                var batch = plans.ToList();
                if (invocation == 1)
                {
                    firstBatch.TrySetResult(batch);
                }
                else
                {
                    secondBatch.TrySetResult(batch);
                }

                var labels = batch.Select(plan => string.IsNullOrWhiteSpace(plan.Label) ? plan.HostId : plan.Label).ToArray();
                var missing = labels.Length > 0 ? new[] { labels[^1] } : Array.Empty<string>();

                var catalog = new[]
                {
                    new ConfigCatalogEntry
                    {
                        ConfigId = "plugins.conf",
                        DisplayName = "plugins.conf",
                        Format = "ini",
                        DriftCount = 0,
                        Severity = "low",
                        PresentHosts = labels.Take(Math.Max(1, labels.Length - 1)).ToArray(),
                        MissingHosts = missing,
                        LastUpdated = DateTimeOffset.UtcNow,
                        CoverageStatus = missing.Length > 0 ? "partial" : "full",
                    },
                };

                var results = batch.Select(plan => new ServerScanResult
                {
                    HostId = plan.HostId,
                    Label = plan.Label,
                    Status = ServerScanStatus.Succeeded,
                    Message = "Completed",
                    Timestamp = DateTimeOffset.UtcNow,
                }).ToArray();

                return Task.FromResult(new ServerScanResponse
                {
                    Results = results,
                    Catalog = catalog,
                });
            },
        };

        var cache = new InMemorySessionCacheService();
        var viewModel = CreateViewModel(fakeService, cache);

        await viewModel.RunAllCommand.ExecuteAsync(null);
        var initialPlans = await firstBatch.Task;
        initialPlans.Should().NotBeEmpty();

        var partial = viewModel.CatalogViewModel.PartialCoverageEntries.First();
        viewModel.CatalogViewModel.ReScanMissingCommand.Execute(partial);

        var rescopedPlans = await secondBatch.Task;
        rescopedPlans.Should().HaveCount(1);
        rescopedPlans[0].Label.Should().Be(partial.MissingHosts.First());
    }

    private static ServerSelectionViewModel CreateViewModel(
        FakeDriftbusterService? service = null,
        InMemorySessionCacheService? cache = null)
    {
        service ??= new FakeDriftbusterService
        {
            RunServerScansHandler = (plans, progress, token) =>
            {
                var list = plans.ToList();
                var labels = list.Select(plan => string.IsNullOrWhiteSpace(plan.Label) ? plan.HostId : plan.Label).ToArray();

                var results = list.Select(plan => new ServerScanResult
                {
                    HostId = plan.HostId,
                    Label = plan.Label,
                    Status = ServerScanStatus.Succeeded,
                    Message = "Completed",
                    Timestamp = DateTimeOffset.UtcNow,
                }).ToArray();

                var catalog = new[]
                {
                    new ConfigCatalogEntry
                    {
                        ConfigId = "appsettings.json",
                        DisplayName = "appsettings.json",
                        Format = "json",
                        DriftCount = 1,
                        Severity = "medium",
                        PresentHosts = labels,
                        MissingHosts = Array.Empty<string>(),
                        LastUpdated = DateTimeOffset.UtcNow,
                        CoverageStatus = "full",
                        HasMaskedTokens = true,
                    },
                };

                return Task.FromResult(new ServerScanResponse
                {
                    Results = results,
                    Catalog = catalog,
                });
            },
        };

        return new ServerSelectionViewModel(service, cache);
    }
}
