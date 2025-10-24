using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;

using DriftBuster.Backend.Models;
using DriftBuster.Gui.Services;
using DriftBuster.Gui.Tests.Fakes;
using DriftBuster.Gui.ViewModels;
using DriftBuster.Gui.Views;

using FluentAssertions;

using Xunit;

namespace DriftBuster.Gui.Tests.Ui;

[Collection(HeadlessCollection.Name)]
public sealed class ServerSelectionViewTests
{
    [AvaloniaFact]
    public void ShouldInitialiseWithDefaultHosts()
    {
        var viewModel = CreateViewModel();

        viewModel.Servers.Should().HaveCount(6);
        viewModel.Servers.Take(3).All(server => server.IsEnabled).Should().BeTrue();
        viewModel.Servers[0].Label.Should().Be("App Inc");
    }

    [AvaloniaFact]
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

    [AvaloniaFact]
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
                    Availability = ServerAvailabilityStatus.Found,
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

                var drilldown = new[]
                {
                    new ConfigDrilldown
                    {
                        ConfigId = "appsettings.json",
                        DisplayName = "appsettings.json",
                        Format = "json",
                        BaselineHostId = list.FirstOrDefault()?.HostId ?? "baseline",
                        DiffBefore = "{\"LogLevel\":\"Information\"}",
                        DiffAfter = "{\"LogLevel\":\"Warning\"}",
                        UnifiedDiff = "- LogLevel: Information\n+ LogLevel: Warning",
                        LastUpdated = DateTimeOffset.UtcNow,
                        DriftCount = 1,
                        Servers = list.Select((plan, index) => new ConfigServerDetail
                        {
                            HostId = plan.HostId,
                            Label = string.IsNullOrWhiteSpace(plan.Label) ? plan.HostId : plan.Label,
                            Present = true,
                            IsBaseline = index == 0,
                            Status = index == 0 ? "Baseline" : "Drift",
                            DriftLineCount = index,
                            HasSecrets = false,
                            Masked = false,
                            RedactionStatus = "Visible",
                            LastSeen = DateTimeOffset.UtcNow.AddMinutes(-index),
                        }).ToArray(),
                        Notes = new[] { "Sample drilldown" },
                        Provenance = "Unit test",
                    }
                };

                return Task.FromResult(new ServerScanResponse { Results = results, Catalog = catalog, Drilldown = drilldown });
            },
        };

        var cache = new InMemorySessionCacheService();
        var viewModel = CreateViewModel(fakeService, cache);

        await viewModel.RunAllCommand.ExecuteAsync(null);

        SpinWait.SpinUntil(
            () => viewModel.Servers[0].RunState == ServerScanStatus.Succeeded,
            TimeSpan.FromSeconds(2)).Should().BeTrue();
        viewModel.Servers[0].StatusText.Should().Be("Completed");
        viewModel.CatalogViewModel.HasEntries.Should().BeTrue();
        viewModel.IsViewingCatalog.Should().BeTrue();
    }

    [AvaloniaFact]
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

    [AvaloniaFact]
    public async Task SaveSessionShouldCaptureCatalogSortDescriptor()
    {
        var cache = new InMemorySessionCacheService();
        var viewModel = CreateViewModel(cache: cache);

        viewModel.PersistSessionState = true;
        viewModel.CatalogViewModel.SetSortDescriptor(CatalogSortColumns.Drift, descending: false);
        viewModel.ActivityFilter = ActivityFilterOption.Exports;

        await viewModel.SaveSessionCommand.ExecuteAsync(null);

        cache.Snapshot.Should().NotBeNull();
        cache.Snapshot!.CatalogSort.Should().NotBeNull();
        cache.Snapshot!.CatalogSort!.Column.Should().Be(CatalogSortColumns.Drift);
        cache.Snapshot.CatalogSort.Descending.Should().BeFalse();
        cache.Snapshot.ActivityFilter.Should().Be(ActivityFilterOption.Exports.ToString());
    }

    [AvaloniaFact]
    public void LoadSessionRestoresCatalogSortDescriptor()
    {
        var cache = new InMemorySessionCacheService
        {
            Snapshot = new ServerSelectionCache
            {
                PersistSession = true,
                CatalogSort = new CatalogSortCache
                {
                    Column = CatalogSortColumns.Format,
                    Descending = false,
                },
                ActivityFilter = ActivityFilterOption.Warnings.ToString(),
            },
        };

        var viewModel = CreateViewModel(cache: cache);

        SpinWait.SpinUntil(() => viewModel.PersistSessionState, TimeSpan.FromSeconds(1)).Should().BeTrue();
        viewModel.CatalogViewModel.SortDescriptor.ColumnKey.Should().Be(CatalogSortColumns.Format);
        viewModel.CatalogViewModel.SortDescriptor.Descending.Should().BeFalse();
        viewModel.ActivityFilter.Should().Be(ActivityFilterOption.Warnings);
    }

    [AvaloniaFact]
    public async Task CopyJsonCommandUpdatesStatus()
    {
        var toast = new ToastService(action => action());
        var viewModel = CreateViewModel(toast: toast);

        await viewModel.RunAllCommand.ExecuteAsync(null);
        var firstEntry = viewModel.CatalogViewModel.FilteredEntries.First();
        viewModel.CatalogViewModel.DrilldownCommand.Execute(firstEntry);

        viewModel.DrilldownViewModel.Should().NotBeNull();
        await viewModel.DrilldownViewModel!.CopyJsonCommand.ExecuteAsync(null);

        viewModel.StatusBanner.Should().Contain("JSON copied");
        toast.ActiveToasts.Should().Contain(toastNotification => toastNotification.Title.Contains("JSON", StringComparison.OrdinalIgnoreCase));
    }

    [AvaloniaFact]
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

    [AvaloniaFact]
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

    [AvaloniaFact]
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
                    Availability = ServerAvailabilityStatus.Found,
                }).ToArray();

                var drilldown = new[]
                {
                    new ConfigDrilldown
                    {
                        ConfigId = "plugins.conf",
                        DisplayName = "plugins.conf",
                        Format = "ini",
                        BaselineHostId = batch.FirstOrDefault()?.HostId ?? "baseline",
                        DiffBefore = "[plugins]\ncore=true",
                        DiffAfter = "[plugins]\ncore=false",
                        UnifiedDiff = "- core=true\n+ core=false",
                        LastUpdated = DateTimeOffset.UtcNow,
                        DriftCount = 0,
                        HasValidationIssues = missing.Length > 0,
                        Servers = batch.Select(plan => new ConfigServerDetail
                        {
                            HostId = plan.HostId,
                            Label = string.IsNullOrWhiteSpace(plan.Label) ? plan.HostId : plan.Label,
                            Present = !missing.Contains(string.IsNullOrWhiteSpace(plan.Label) ? plan.HostId : plan.Label, StringComparer.OrdinalIgnoreCase),
                            IsBaseline = plan.HostId == (batch.FirstOrDefault()?.HostId ?? string.Empty),
                            Status = missing.Contains(plan.Label ?? plan.HostId, StringComparer.OrdinalIgnoreCase) ? "Missing" : "Match",
                            DriftLineCount = 0,
                            HasSecrets = false,
                            Masked = false,
                            RedactionStatus = "Visible",
                            LastSeen = DateTimeOffset.UtcNow,
                        }).ToArray(),
                        Notes = new[] { "plugins.conf missing on one host" },
                        Provenance = "Unit test",
                    }
                };

                return Task.FromResult(new ServerScanResponse
                {
                    Results = results,
                    Catalog = catalog,
                    Drilldown = drilldown,
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

    [AvaloniaFact]
    public async Task DrilldownShouldSupportExportAndRescan()
    {
        var exported = new List<ConfigDrilldownExportRequest>();
        var rescans = new TaskCompletionSource<IReadOnlyList<ServerScanPlan>>();
        var invocation = 0;
        var service = new FakeDriftbusterService
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
                    Availability = ServerAvailabilityStatus.Found,
                }).ToArray();

                var catalog = new[]
                {
                    new ConfigCatalogEntry
                    {
                        ConfigId = "appsettings.json",
                        DisplayName = "appsettings.json",
                        Format = "json",
                        DriftCount = 2,
                        Severity = "high",
                        PresentHosts = labels,
                        MissingHosts = Array.Empty<string>(),
                        LastUpdated = DateTimeOffset.UtcNow,
                        CoverageStatus = "full",
                    },
                };

                var drilldown = new[]
                {
                    new ConfigDrilldown
                    {
                        ConfigId = "appsettings.json",
                        DisplayName = "appsettings.json",
                        Format = "json",
                        BaselineHostId = list.First().HostId,
                        DiffBefore = "{\"LogLevel\":\"Information\"}",
                        DiffAfter = "{\"LogLevel\":\"Warning\"}",
                        UnifiedDiff = "- LogLevel: Information\n+ LogLevel: Warning",
                        LastUpdated = DateTimeOffset.UtcNow,
                        DriftCount = 2,
                        Notes = new[] { "Investigate log level drift" },
                        Provenance = "Unit test",
                        HasMaskedTokens = true,
                        Servers = list.Select((plan, index) => new ConfigServerDetail
                        {
                            HostId = plan.HostId,
                            Label = string.IsNullOrWhiteSpace(plan.Label) ? plan.HostId : plan.Label,
                            Present = true,
                            IsBaseline = index == 0,
                            Status = index == 0 ? "Baseline" : "Drift",
                            DriftLineCount = index + 1,
                            HasSecrets = index == 1,
                            Masked = index % 2 == 0,
                            RedactionStatus = index % 2 == 0 ? "Masked" : "Visible",
                            LastSeen = DateTimeOffset.UtcNow,
                        }).ToArray(),
                    }
                };

                if (invocation++ > 0)
                {
                    rescans.TrySetResult(list);
                }

                return Task.FromResult(new ServerScanResponse
                {
                    Results = results,
                    Catalog = catalog,
                    Drilldown = drilldown,
                });
            },
        };

        var cache = new InMemorySessionCacheService();
        var viewModel = CreateViewModel(service, cache);
        viewModel.ExportCallback = request =>
        {
            exported.Add(request);
            return Task.CompletedTask;
        };

        await viewModel.RunAllCommand.ExecuteAsync(null);

        var item = viewModel.CatalogViewModel.FilteredEntries.First();
        viewModel.CatalogViewModel.DrilldownCommand.Execute(item);

        viewModel.DrilldownViewModel.Should().NotBeNull();
        viewModel.IsViewingDrilldown.Should().BeTrue();

        await viewModel.DrilldownViewModel!.ExportHtmlCommand.ExecuteAsync(null);
        exported.Should().ContainSingle(req => req.Format == ConfigDrilldownViewModel.ExportFormat.Html);

        viewModel.DrilldownViewModel.SelectNoneCommand.Execute(null);
        viewModel.DrilldownViewModel.Servers.First().IsSelected = true;
        viewModel.DrilldownViewModel.ReScanSelectedCommand.Execute(null);

        var rescanned = await rescans.Task;
        rescanned.Should().ContainSingle(plan => plan.HostId == viewModel.DrilldownViewModel.Servers.First().HostId);
    }

    [AvaloniaFact]
    public async Task RunAllProducesTimelineAndToast()
    {
        var toast = new ToastService(action => action());
        var viewModel = CreateViewModel(toast: toast);

        await viewModel.RunAllCommand.ExecuteAsync(null);

        viewModel.FilteredActivityEntries.Should().NotBeEmpty();
        toast.ActiveToasts.Should().NotBeEmpty();
    }

    [AvaloniaFact]
    public async Task CopyActivityCommandRaisesClipboardEvent()
    {
        var toast = new ToastService(action => action());
        var viewModel = CreateViewModel(toast: toast);
        string? copied = null;
        viewModel.CopyActivityRequested += (_, text) => copied = text;

        await viewModel.RunAllCommand.ExecuteAsync(null);

        var firstEntry = viewModel.FilteredActivityEntries.FirstOrDefault();
        firstEntry.Should().NotBeNull();
        viewModel.CopyActivityCommand.Execute(firstEntry!);

        copied.Should().NotBeNullOrWhiteSpace();
    }

    [AvaloniaFact]
    public async Task CancelRunsEmitsActivityAndToast()
    {
        var tcs = new TaskCompletionSource<ServerScanResponse>();
        var service = new FakeDriftbusterService
        {
            RunServerScansHandler = (plans, progress, token) =>
            {
                token.Register(() => tcs.TrySetCanceled(token));
                return tcs.Task;
            },
        };

        var toast = new ToastService(action => action());
        var viewModel = CreateViewModel(service, toast: toast);

        var runTask = viewModel.RunAllCommand.ExecuteAsync(null);
        viewModel.CancelRunsCommand.Execute(null);

        await runTask;

        toast.ActiveToasts.Should().NotBeEmpty();
        viewModel.FilteredActivityEntries.Should().Contain(entry => entry.Summary.Contains("cancelled", StringComparison.OrdinalIgnoreCase));
        viewModel.IsBusy.Should().BeFalse();
    }

    [AvaloniaFact]
    public void Virtualization_components_toggle_visibility()
    {
        var profile = new PerformanceProfile(virtualizationThreshold: 2);
        var toast = new ToastService(action => action());
        var viewModel = new ServerSelectionViewModel(new FakeDriftbusterService(), toast, performanceProfile: profile);

        var logActivity = typeof(ServerSelectionViewModel)
            .GetMethod("LogActivity", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        logActivity!.Invoke(viewModel, new object?[] { ActivitySeverity.Info, "first", null, ActivityCategory.General });
        logActivity.Invoke(viewModel, new object?[] { ActivitySeverity.Info, "second", null, ActivityCategory.General });

        var view = new ServerSelectionView
        {
            DataContext = viewModel,
        };

        view.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        view.Arrange(new Rect(view.DesiredSize));
        view.UpdateLayout();

        var serverRepeater = view.FindControl<ItemsRepeater>("ServerCardsVirtualRepeater");
        var serverFallback = view.FindControl<ItemsControl>("ServerCardsFallback");
        serverRepeater!.IsVisible.Should().BeTrue();
        serverFallback!.IsVisible.Should().BeFalse();

        var activityRepeater = view.FindControl<ItemsRepeater>("ActivityVirtualRepeater");
        var activityFallback = view.FindControl<ItemsControl>("ActivityFallback");
        activityRepeater!.IsVisible.Should().BeTrue();
        activityFallback!.IsVisible.Should().BeFalse();

        var conservativeProfile = new PerformanceProfile(virtualizationThreshold: 1000);
        var nonVirtualViewModel = new ServerSelectionViewModel(new FakeDriftbusterService(), toast, performanceProfile: conservativeProfile);
        var nonVirtualView = new ServerSelectionView
        {
            DataContext = nonVirtualViewModel,
        };

        nonVirtualView.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        nonVirtualView.Arrange(new Rect(nonVirtualView.DesiredSize));
        nonVirtualView.UpdateLayout();

        var nonVirtualServerRepeater = nonVirtualView.FindControl<ItemsRepeater>("ServerCardsVirtualRepeater");
        var nonVirtualServerFallback = nonVirtualView.FindControl<ItemsControl>("ServerCardsFallback");
        nonVirtualServerRepeater!.IsVisible.Should().BeFalse();
        nonVirtualServerFallback!.IsVisible.Should().BeTrue();

        var nonVirtualActivityRepeater = nonVirtualView.FindControl<ItemsRepeater>("ActivityVirtualRepeater");
        var nonVirtualActivityFallback = nonVirtualView.FindControl<ItemsControl>("ActivityFallback");
        nonVirtualActivityRepeater!.IsVisible.Should().BeFalse();
        nonVirtualActivityFallback!.IsVisible.Should().BeTrue();
    }

    private static ServerSelectionViewModel CreateViewModel(
        FakeDriftbusterService? service = null,
        InMemorySessionCacheService? cache = null,
        ToastService? toast = null)
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
                    Availability = ServerAvailabilityStatus.Found,
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

                var drilldown = new[]
                {
                    new ConfigDrilldown
                    {
                        ConfigId = "appsettings.json",
                        DisplayName = "appsettings.json",
                        Format = "json",
                        BaselineHostId = list.FirstOrDefault()?.HostId ?? "baseline",
                        DiffBefore = "{\"LogLevel\":\"Information\"}",
                        DiffAfter = "{\"LogLevel\":\"Warning\"}",
                        UnifiedDiff = "- LogLevel: Information\n+ LogLevel: Warning",
                        HasMaskedTokens = true,
                        LastUpdated = DateTimeOffset.UtcNow,
                        DriftCount = 1,
                        Servers = list.Select((plan, index) => new ConfigServerDetail
                        {
                            HostId = plan.HostId,
                            Label = string.IsNullOrWhiteSpace(plan.Label) ? plan.HostId : plan.Label,
                            Present = true,
                            IsBaseline = index == 0,
                            Status = index == 0 ? "Baseline" : "Drift",
                            DriftLineCount = index,
                            HasSecrets = index % 2 == 0,
                            Masked = index % 3 == 0,
                            RedactionStatus = index % 3 == 0 ? "Masked" : "Visible",
                            LastSeen = DateTimeOffset.UtcNow.AddMinutes(-index),
                        }).ToArray(),
                        Notes = new[] { "Sample drilldown export" },
                        Provenance = "Unit test",
                    }
                };

                return Task.FromResult(new ServerScanResponse
                {
                    Results = results,
                    Catalog = catalog,
                    Drilldown = drilldown,
                });
            },
        };

        toast ??= new ToastService(action => action());
        var viewModel = new ServerSelectionViewModel(service, toast, cache);

        var defaultRoot = AppContext.BaseDirectory;
        foreach (var server in viewModel.Servers)
        {
            if (!server.IsEnabled)
            {
                continue;
            }

            server.ReplaceRoots(new[]
            {
                new RootEntryViewModel(defaultRoot)
            });
        }

        return viewModel;
    }
}