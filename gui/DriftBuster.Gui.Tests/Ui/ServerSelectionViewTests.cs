using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using AwesomeAssertions;
using DriftBuster.Backend.Models;
using DriftBuster.Gui.Services;
using DriftBuster.Gui.Tests.Fakes;
using DriftBuster.Gui.ViewModels;
using DriftBuster.Gui.Views;
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
    public async Task Navigation_and_run_all_controls_stay_available()
    {
        var viewModel = CreateViewModel();
        var view = new ServerSelectionView
        {
            DataContext = viewModel,
        };

        view.Measure(new Size(1200, 900));
        view.Arrange(new Rect(view.DesiredSize));
        view.UpdateLayout();

        var setupButton = view.FindControl<Button>("SetupButton");
        var headerRunAllButton = view.FindControl<Button>("HeaderRunAllButton");

        setupButton.Should().NotBeNull();
        headerRunAllButton.Should().NotBeNull();
        setupButton!.IsEnabled.Should().BeTrue();
        headerRunAllButton!.IsEnabled.Should().BeTrue();

        await viewModel.RunAllCommand.ExecuteAsync(null);

        viewModel.IsViewingCatalog.Should().BeTrue();
        setupButton.IsEnabled.Should().BeTrue();
        headerRunAllButton.IsEnabled.Should().BeTrue();
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

#pragma warning disable MA0051
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
#pragma warning restore MA0051

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
    public async Task ServerCardPointerPressInitiatesDragWithHostMetadata()
    {
        var viewModel = CreateViewModel();

        var view = new ServerSelectionView
        {
            DataContext = viewModel,
        };

        var harness = new RecordingDragDropService();
        view.DragDropService = harness;

        view.Measure(new Size(1024, 768));
        view.Arrange(new Rect(view.DesiredSize));
        view.UpdateLayout();

        var slotViewModel = viewModel.Servers.First(server => server.IsEnabled);
        var card = new Border
        {
            DataContext = slotViewModel,
        };
        var cardSize = new Size(320, 180);
        card.Measure(cardSize);
        card.Arrange(new Rect(cardSize));

        var pointer = new Avalonia.Input.Pointer(1, PointerType.Mouse, true);
        var properties = new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed);
        var args = new PointerPressedEventArgs(card, pointer, view, new Point(10, 10), 0, properties, KeyModifiers.None, 1);

        var invocation = view.HandleServerCardPointerPressedAsync(card, args);

        harness.LastArgs.Should().NotBeNull();
        harness.LastData.Should().NotBeNull();
        harness.LastEffects.Should().Be(DragDropEffects.Move);

        var serverSlotFormat = DataFormat.CreateStringApplicationFormat("driftbuster.server-slot");
        var asyncData = (IAsyncDataTransfer)harness.LastData!;
        asyncData.Contains(serverSlotFormat).Should().BeTrue();
        asyncData.Contains(DataFormat.Text).Should().BeTrue();

        var hostId = await asyncData.TryGetValueAsync(serverSlotFormat).ConfigureAwait(false);
        hostId.Should().Be(slotViewModel.HostId);
        var label = await asyncData.TryGetValueAsync(DataFormat.Text).ConfigureAwait(false);
        label.Should().Be(slotViewModel.Label);

        harness.Complete(DragDropEffects.Move);
        await invocation.ConfigureAwait(false);
    }

    [AvaloniaFact]
    public async Task DragDropExceptionsBubbleToCaller()
    {
        var viewModel = CreateViewModel();

        var view = new ServerSelectionView
        {
            DataContext = viewModel,
        };

        var harness = new ThrowingDragDropService(new InvalidOperationException("drag failed"));
        view.DragDropService = harness;

        view.Measure(new Size(1024, 768));
        view.Arrange(new Rect(view.DesiredSize));
        view.UpdateLayout();

        var slotViewModel = viewModel.Servers.First(server => server.IsEnabled);
        var card = new Border
        {
            DataContext = slotViewModel,
        };
        var cardSize = new Size(320, 180);
        card.Measure(cardSize);
        card.Arrange(new Rect(cardSize));

        var pointer = new Avalonia.Input.Pointer(1, PointerType.Mouse, true);
        var properties = new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed);
        var args = new PointerPressedEventArgs(card, pointer, view, new Point(10, 10), 0, properties, KeyModifiers.None, 1);

        var action = () => view.HandleServerCardPointerPressedAsync(card, args);

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("drag failed");
    }

    [AvaloniaFact]
    public async Task PointerPressFromInteractiveChildDoesNotStartDrag()
    {
        var viewModel = CreateViewModel();

        var view = new ServerSelectionView
        {
            DataContext = viewModel,
        };

        var harness = new RecordingDragDropService();
        view.DragDropService = harness;

        var slotViewModel = viewModel.Servers.First(server => server.IsEnabled);
        var card = new Border
        {
            DataContext = slotViewModel,
        };
        var childTextBox = new TextBox();
        card.Child = childTextBox;

        var cardSize = new Size(320, 180);
        card.Measure(cardSize);
        card.Arrange(new Rect(cardSize));
        childTextBox.Measure(new Size(200, 32));
        childTextBox.Arrange(new Rect(0, 0, 200, 32));

        var pointer = new Avalonia.Input.Pointer(1, PointerType.Mouse, true);
        var properties = new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed);
        var args = new PointerPressedEventArgs(childTextBox, pointer, view, new Point(10, 10), 0, properties, KeyModifiers.None, 1);

        await view.HandleServerCardPointerPressedAsync(card, args);

        harness.LastArgs.Should().BeNull();
        harness.LastData.Should().BeNull();
    }

    [AvaloniaFact]
    public void LowerHalfDropMovesHostAfterTarget()
    {
        var viewModel = CreateViewModel();
        var view = new ServerSelectionView
        {
            DataContext = viewModel,
        };

        view.Measure(new Size(1024, 768));
        view.Arrange(new Rect(view.DesiredSize));
        view.UpdateLayout();

        var sourceSlot = viewModel.Servers[0];
        var targetSlot = viewModel.Servers[1];

        var dropHandler = typeof(ServerSelectionView)
            .GetMethod("OnServerCardDrop", BindingFlags.Instance | BindingFlags.NonPublic);
        dropHandler.Should().NotBeNull();

        var targetCard = new Border
        {
            DataContext = targetSlot,
        };
        var layoutSize = new Size(320, 160);
        targetCard.Measure(layoutSize);
        targetCard.Arrange(new Rect(layoutSize));

        var serverSlotFormat = DataFormat.CreateStringApplicationFormat("driftbuster.server-slot");
        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(serverSlotFormat, sourceSlot.HostId));
        data.Add(DataTransferItem.Create(DataFormat.Text, sourceSlot.Label));

        var dropPoint = new Point(layoutSize.Width / 2, layoutSize.Height * 0.75);
        var dropArgs = new DragEventArgs(DragDrop.DropEvent, data, targetCard, dropPoint, KeyModifiers.None);

        dropHandler!.Invoke(view, new object?[] { targetCard, dropArgs });

        dropArgs.Handled.Should().BeTrue();
        dropArgs.DragEffects.Should().Be(DragDropEffects.Move);

        viewModel.Servers[0].HostId.Should().Be(targetSlot.HostId);
        viewModel.Servers[1].HostId.Should().Be(sourceSlot.HostId);
        viewModel.Servers.Select(slot => slot.Index).Should().Equal(Enumerable.Range(0, viewModel.Servers.Count));
    }

    [AvaloniaFact]
    public void ShouldAdjustResponsiveSpacingTokens()
    {
        var view = new ServerSelectionView
        {
            DataContext = CreateViewModel(),
        };

        ResponsiveLayoutService.Apply(view, 1200, ResponsiveSpacingProfiles.ServerSelection);
        view.Resources["ServerSelection.OuterMargin"].Should().Be(new Thickness(0, 0, 0, 24));
        view.Resources["ServerSelection.StackSpacing"].Should().Be(18d);

        ResponsiveLayoutService.Apply(view, 1400, ResponsiveSpacingProfiles.ServerSelection);
        view.Resources["ServerSelection.OuterMargin"].Should().Be(new Thickness(0, 0, 0, 28));
        view.Resources["ServerSelection.StackSpacing"].Should().Be(20d);

        ResponsiveLayoutService.Apply(view, 1700, ResponsiveSpacingProfiles.ServerSelection);
        view.Resources["ServerSelection.OuterMargin"].Should().Be(new Thickness(0, 0, 0, 32));
        view.Resources["ServerSelection.StackSpacing"].Should().Be(22d);

        ResponsiveLayoutService.Apply(view, 2100, ResponsiveSpacingProfiles.ServerSelection);
        view.Resources["ServerSelection.OuterMargin"].Should().Be(new Thickness(0, 0, 0, 36));
        view.Resources["ServerSelection.StackSpacing"].Should().Be(24d);
    }

    [AvaloniaFact]
    public void DataContextChanged_rebinds_view_model_subscription()
    {
        var first = CreateViewModel();
        var second = CreateViewModel();
        var view = new ServerSelectionView();

        view.DataContext = first;
        ReadAttachedViewModel(view).Should().BeSameAs(first);

        view.DataContext = second;
        ReadAttachedViewModel(view).Should().BeSameAs(second);
    }

    [AvaloniaFact]
    public void CopyActivityRequested_ignores_blank_payload()
    {
        var view = new ServerSelectionView
        {
            DataContext = CreateViewModel(),
        };

        var method = typeof(ServerSelectionView).GetMethod("OnCopyActivityRequested", BindingFlags.Instance | BindingFlags.NonPublic);
        method.Should().NotBeNull();
        method!.Invoke(view, new object?[] { null, new ValueEventArgs<string>(" ") });
    }

    [AvaloniaFact]
    public void DragOver_sets_move_effect_for_valid_reorder_and_none_for_invalid()
    {
        var viewModel = CreateViewModel();
        var view = new ServerSelectionView
        {
            DataContext = viewModel,
        };

        var sourceSlot = viewModel.Servers[0];
        var targetSlot = viewModel.Servers[1];
        var targetCard = new Border
        {
            DataContext = targetSlot,
        };
        var layoutSize = new Size(320, 160);
        targetCard.Measure(layoutSize);
        targetCard.Arrange(new Rect(layoutSize));

        var dragOver = typeof(ServerSelectionView)
            .GetMethod("OnServerCardDragOver", BindingFlags.Instance | BindingFlags.NonPublic);
        dragOver.Should().NotBeNull();

        var serverSlotFormat = DataFormat.CreateStringApplicationFormat("driftbuster.server-slot");

        var validTransfer = new DataTransfer();
        validTransfer.Add(DataTransferItem.Create(serverSlotFormat, sourceSlot.HostId));
        var validArgs = new DragEventArgs(DragDrop.DragOverEvent, validTransfer, targetCard, new Point(10, 10), KeyModifiers.None);

        dragOver!.Invoke(view, new object?[] { targetCard, validArgs });
        validArgs.Handled.Should().BeTrue();
        validArgs.DragEffects.Should().Be(DragDropEffects.Move);

        var invalidTransfer = new DataTransfer();
        invalidTransfer.Add(DataTransferItem.Create(serverSlotFormat, targetSlot.HostId));
        var invalidArgs = new DragEventArgs(DragDrop.DragOverEvent, invalidTransfer, targetCard, new Point(10, 10), KeyModifiers.None);

        dragOver.Invoke(view, new object?[] { targetCard, invalidArgs });
        invalidArgs.Handled.Should().BeTrue();
        invalidArgs.DragEffects.Should().Be(DragDropEffects.None);
    }

    [AvaloniaFact]
    public void PointerPressed_wrapper_ignores_non_left_or_busy_or_invalid_sender()
    {
        var viewModel = CreateViewModel();
        var view = new ServerSelectionView
        {
            DataContext = viewModel,
        };
        var harness = new RecordingDragDropService();
        view.DragDropService = harness;

        var method = typeof(ServerSelectionView)
            .GetMethod("OnServerCardPointerPressed", BindingFlags.Instance | BindingFlags.NonPublic);
        method.Should().NotBeNull();

        var pointer = new Avalonia.Input.Pointer(3, PointerType.Mouse, true);
        var rightClick = new PointerPointProperties(RawInputModifiers.RightMouseButton, PointerUpdateKind.RightButtonPressed);
        var nonLeftArgs = new PointerPressedEventArgs(view, pointer, view, new Point(8, 8), 0, rightClick, KeyModifiers.None, 1);
        method!.Invoke(view, new object?[] { view, nonLeftArgs });
        harness.LastArgs.Should().BeNull();

        viewModel.IsBusy = true;
        var leftClick = new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed);
        var busyArgs = new PointerPressedEventArgs(view, pointer, view, new Point(10, 10), 0, leftClick, KeyModifiers.None, 1);
        method.Invoke(view, new object?[] { view, busyArgs });
        harness.LastArgs.Should().BeNull();

        viewModel.IsBusy = false;
        method.Invoke(view, new object?[] { new Border(), busyArgs });
        harness.LastArgs.Should().BeNull();
    }

    [AvaloniaFact]
    public void Drag_handlers_ignore_invalid_payloads()
    {
        var viewModel = CreateViewModel();
        var view = new ServerSelectionView
        {
            DataContext = viewModel,
        };

        var target = new Border
        {
            DataContext = viewModel.Servers[0],
        };
        target.Measure(new Size(320, 160));
        target.Arrange(new Rect(new Size(320, 160)));

        var dragOver = typeof(ServerSelectionView)
            .GetMethod("OnServerCardDragOver", BindingFlags.Instance | BindingFlags.NonPublic);
        var drop = typeof(ServerSelectionView)
            .GetMethod("OnServerCardDrop", BindingFlags.Instance | BindingFlags.NonPublic);
        dragOver.Should().NotBeNull();
        drop.Should().NotBeNull();

        var emptyData = new DataTransfer();
        var overArgs = new DragEventArgs(DragDrop.DragOverEvent, emptyData, target, new Point(4, 4), KeyModifiers.None);
        dragOver!.Invoke(view, new object?[] { target, overArgs });
        overArgs.DragEffects.Should().Be(DragDropEffects.None);

        var dropArgs = new DragEventArgs(DragDrop.DropEvent, emptyData, target, new Point(10, 10), KeyModifiers.None);
        drop!.Invoke(view, new object?[] { target, dropArgs });
        dropArgs.Handled.Should().BeTrue();
        dropArgs.DragEffects.Should().Be(DragDropEffects.None);
    }

    private static ServerSelectionViewModel? ReadAttachedViewModel(ServerSelectionView view)
    {
        var field = typeof(ServerSelectionView).GetField("_viewModel", BindingFlags.Instance | BindingFlags.NonPublic);
        field.Should().NotBeNull();
        return field!.GetValue(view) as ServerSelectionViewModel;
    }

    private sealed class RecordingDragDropService : IDragDropService
    {
        private readonly TaskCompletionSource<DragDropEffects> _completion = new();

        public PointerEventArgs? LastArgs { get; private set; }

        public IDataTransfer? LastData { get; private set; }

        public DragDropEffects? LastEffects { get; private set; }

        public Task<DragDropEffects> DoDragDropAsync(PointerEventArgs args, IDataTransfer data, DragDropEffects effects)
        {
            LastArgs = args;
            LastData = data;
            LastEffects = effects;
            return _completion.Task;
        }

        public void Complete(DragDropEffects result) => _completion.TrySetResult(result);
    }

    private sealed class ThrowingDragDropService : IDragDropService
    {
        private readonly Exception _exception;

        public ThrowingDragDropService(Exception exception)
        {
            _exception = exception;
        }

        public Task<DragDropEffects> DoDragDropAsync(PointerEventArgs args, IDataTransfer data, DragDropEffects effects)
        {
            return Task.FromException<DragDropEffects>(_exception);
        }
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

#pragma warning disable MA0051
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
                            IsBaseline = string.Equals(plan.HostId, batch.FirstOrDefault()?.HostId ?? string.Empty, StringComparison.Ordinal),
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
#pragma warning restore MA0051

#pragma warning disable MA0051
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
#pragma warning restore MA0051

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
    public async Task RunAll_with_failed_results_does_not_report_invalid_thread()
    {
        var service = new FakeDriftbusterService
        {
            RunServerScansHandler = (plans, progress, _) =>
            {
                var plan = plans.First();
                progress?.Report(new ScanProgress
                {
                    HostId = plan.HostId,
                    Status = ServerScanStatus.Queued,
                    Message = "Queued",
                    Timestamp = DateTimeOffset.UtcNow,
                });
                progress?.Report(new ScanProgress
                {
                    HostId = plan.HostId,
                    Status = ServerScanStatus.Running,
                    Message = $"Scanning {plan.Label}",
                    Timestamp = DateTimeOffset.UtcNow,
                });
                progress?.Report(new ScanProgress
                {
                    HostId = plan.HostId,
                    Status = ServerScanStatus.Failed,
                    Message = "Scan failed: Unknown catalog variant 'log4net-config' for format 'structured-config-xml'.",
                    Timestamp = DateTimeOffset.UtcNow,
                });

                return Task.FromResult(new ServerScanResponse
                {
                    Version = "multi-server.v1",
                    Results = new[]
                    {
                        new ServerScanResult
                        {
                            HostId = plan.HostId,
                            Label = plan.Label,
                            Status = ServerScanStatus.Failed,
                            Message = "Scan failed: Unknown catalog variant 'log4net-config' for format 'structured-config-xml'.",
                            Timestamp = DateTimeOffset.UtcNow,
                            Availability = ServerAvailabilityStatus.Found,
                        },
                    },
                    Catalog = Array.Empty<ConfigCatalogEntry>(),
                    Drilldown = Array.Empty<ConfigDrilldown>(),
                });
            },
        };

        var viewModel = new ServerSelectionViewModel(service, new ToastService(action => action()), new InMemorySessionCacheService());
        for (var index = 1; index < viewModel.Servers.Count; index++)
        {
            viewModel.Servers[index].IsEnabled = false;
        }

        var host = viewModel.Servers[0];
        host.Scope = ServerScanScope.CustomRoots;
        host.ReplaceRoots(new[] { new RootEntryViewModel(AppContext.BaseDirectory) });

        var view = new ServerSelectionView
        {
            DataContext = viewModel,
        };
        view.Measure(new Size(1200, 900));
        view.Arrange(new Rect(view.DesiredSize));
        view.UpdateLayout();

        await viewModel.RunAllCommand.ExecuteAsync(null);

        viewModel.Servers[0].RunState.Should().Be(ServerScanStatus.Failed);
        viewModel.StatusBanner.ToLowerInvariant().Should().NotContain("invalid thread");
    }

    [AvaloniaFact]
    public async Task CopyActivityCommandRaisesClipboardEvent()
    {
        var toast = new ToastService(action => action());
        var viewModel = CreateViewModel(toast: toast);
        string? copied = null;
        viewModel.CopyActivityRequested += (_, e) => copied = e.Value;

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
    public void Server_cards_surface_validation_summaries_in_tooltips()
    {
        var viewModel = CreateViewModel();
        viewModel.UseVirtualizedServerList = false;

        var view = new ServerSelectionView
        {
            DataContext = viewModel,
        };

        var fallback = view.FindControl<ItemsControl>("ServerCardsFallback");
        fallback.Should().NotBeNull();
        fallback!.ItemTemplate.Should().NotBeNull();

        var slot = viewModel.Servers[0];
        var template = (IDataTemplate)fallback.ItemTemplate!;
        var card = (Border)template.Build(slot)!;
        card.DataContext = slot;

        ToolTip.GetTip(card).Should().Be(slot.ValidationSummary);
        AutomationProperties.GetName(card).Should().Be(slot.ValidationSummary);

        slot.RootInputError = "Absolute root path required.";

        SpinWait.SpinUntil(
                () => string.Equals(ToolTip.GetTip(card) as string, slot.ValidationSummary, StringComparison.Ordinal),
                TimeSpan.FromSeconds(1))
            .Should().BeTrue();
        AutomationProperties.GetName(card).Should().Be(slot.ValidationSummary);

        slot.RootInputError = null;
        foreach (var root in slot.Roots)
        {
            root.ValidationState = RootValidationState.Pending;
            root.StatusMessage = string.Empty;
        }
        slot.RefreshValidationSummary();

        SpinWait.SpinUntil(
                () => string.Equals(ToolTip.GetTip(card) as string, slot.ValidationSummary, StringComparison.Ordinal),
                TimeSpan.FromSeconds(1))
            .Should().BeTrue();
        AutomationProperties.GetName(card).Should().Be(slot.ValidationSummary);
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

        var serverRepeater = view.FindControl<ItemsControl>("ServerCardsVirtualRepeater");
        var serverFallback = view.FindControl<ItemsControl>("ServerCardsFallback");
        serverRepeater!.IsVisible.Should().BeTrue();
        serverFallback!.IsVisible.Should().BeFalse();

        var activityRepeater = view.FindControl<ItemsControl>("ActivityVirtualRepeater");
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

        var nonVirtualServerRepeater = nonVirtualView.FindControl<ItemsControl>("ServerCardsVirtualRepeater");
        var nonVirtualServerFallback = nonVirtualView.FindControl<ItemsControl>("ServerCardsFallback");
        nonVirtualServerRepeater!.IsVisible.Should().BeFalse();
        nonVirtualServerFallback!.IsVisible.Should().BeTrue();

        var nonVirtualActivityRepeater = nonVirtualView.FindControl<ItemsControl>("ActivityVirtualRepeater");
        var nonVirtualActivityFallback = nonVirtualView.FindControl<ItemsControl>("ActivityFallback");
        nonVirtualActivityRepeater!.IsVisible.Should().BeFalse();
        nonVirtualActivityFallback!.IsVisible.Should().BeTrue();
    }

#pragma warning disable MA0051
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
#pragma warning restore MA0051
}
