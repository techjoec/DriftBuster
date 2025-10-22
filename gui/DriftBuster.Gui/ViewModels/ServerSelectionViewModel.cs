using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using DriftBuster.Backend.Models;
using DriftBuster.Gui.Services;

namespace DriftBuster.Gui.ViewModels
{
    public enum RootValidationState
    {
        Pending,
        Valid,
        Invalid,
    }

    public enum ActivityFilterOption
    {
        All,
        Errors,
        Warnings,
        Exports,
    }

    public sealed class ScanScopeOption
    {
        public ScanScopeOption(ServerScanScope value, string displayName)
        {
            Value = value;
            DisplayName = displayName;
        }

        public ServerScanScope Value { get; }

        public string DisplayName { get; }
    }

    public sealed partial class RootEntryViewModel : ObservableObject
    {
        public RootEntryViewModel(string path)
        {
            _path = path.Trim();
        }

        [ObservableProperty]
        private string _path = string.Empty;

        [ObservableProperty]
        private RootValidationState _validationState = RootValidationState.Pending;

        [ObservableProperty]
        private string _statusMessage = string.Empty;
    }

    public sealed partial class ServerSlotViewModel : ObservableObject
    {
        private readonly ServerSelectionViewModel _owner;
        private bool _suppressRootEvents;

        public ServerSlotViewModel(ServerSelectionViewModel owner, int index, string label, bool isEnabled)
        {
            _owner = owner;
            Index = index;
            HostId = $"host-{index + 1:00}";
            _label = label;
            _isEnabled = isEnabled;
            Roots.CollectionChanged += OnRootsChanged;
            if (isEnabled)
            {
                EnsureDefaultRoot();
            }

            RefreshValidationSummary();
        }

        public int Index { get; }

        public string HostId { get; }

        public ObservableCollection<RootEntryViewModel> Roots { get; } = new();

        [ObservableProperty]
        private bool _isEnabled;

        [ObservableProperty]
        private string _label = string.Empty;

        [ObservableProperty]
        private ServerScanScope _scope = ServerScanScope.AllDrives;

        [ObservableProperty]
        private ServerScanStatus _runState = ServerScanStatus.Idle;

        [ObservableProperty]
        private string _statusText = "Idle";

        [ObservableProperty]
        private DateTimeOffset? _lastRunAt;

        [ObservableProperty]
        private string _newRootPath = string.Empty;

        [ObservableProperty]
        private string? _rootInputError;

        [ObservableProperty]
        private string _validationSummary = "All roots ready.";

        public bool CanRetry => RunState == ServerScanStatus.Failed;

        public bool HasCachedResult => RunState is ServerScanStatus.Cached or ServerScanStatus.Succeeded;

        partial void OnRootInputErrorChanged(string? value)
        {
            RefreshValidationSummary();
        }

        partial void OnIsEnabledChanged(bool value)
        {
            if (value)
            {
                EnsureDefaultRoot();
                MarkState(ServerScanStatus.Idle, "Ready");
            }
            else
            {
                MarkState(ServerScanStatus.Skipped, "Disabled");
            }

            RefreshValidationSummary();
            _owner.NotifyServerToggled(this);
        }

        partial void OnScopeChanged(ServerScanScope value)
        {
            _owner.NotifyScopeChanged(this);
        }

        public void MarkState(ServerScanStatus state, string statusText, DateTimeOffset? timestamp = null)
        {
            RunState = state;
            StatusText = statusText;
            if (state is ServerScanStatus.Succeeded or ServerScanStatus.Cached)
            {
                LastRunAt = timestamp ?? DateTimeOffset.UtcNow;
            }
        }

        public void ResetStatus()
        {
            RunState = IsEnabled ? ServerScanStatus.Idle : ServerScanStatus.Skipped;
            StatusText = IsEnabled ? "Idle" : "Disabled";
            if (!IsEnabled)
            {
                LastRunAt = null;
            }
        }

        internal void RefreshValidationSummary()
        {
            ValidationSummary = BuildValidationSummary();
        }

        private string BuildValidationSummary()
        {
            if (!string.IsNullOrWhiteSpace(RootInputError))
            {
                return RootInputError!;
            }

            if (!IsEnabled)
            {
                return "Host disabled; validation paused.";
            }

            if (Roots.Count == 0)
            {
                return "No roots configured.";
            }

            var invalidMessages = Roots
                .Where(root => root.ValidationState == RootValidationState.Invalid)
                .Select(root => string.IsNullOrWhiteSpace(root.StatusMessage) ? "Root requires attention." : root.StatusMessage!)
                .Distinct()
                .ToList();

            if (invalidMessages.Count > 0)
            {
                return string.Join(" ", invalidMessages);
            }

            var pendingCount = Roots.Count(root => root.ValidationState == RootValidationState.Pending);
            if (pendingCount > 0)
            {
                return pendingCount == 1
                    ? "One root pending validation."
                    : $"{pendingCount} roots pending validation.";
            }

            return "All roots ready.";
        }

        internal void EnsureDefaultRoot()
        {
            if (Roots.Count > 0)
            {
                return;
            }

            _suppressRootEvents = true;
            try
            {
                Roots.Add(new RootEntryViewModel(ServerSelectionViewModel.DefaultRootPath));
            }
            finally
            {
                _suppressRootEvents = false;
            }

            _owner.RevalidateRoots(this);
        }

        internal void AddRoot(RootEntryViewModel entry)
        {
            Roots.Add(entry);
        }

        internal void RemoveRoot(RootEntryViewModel entry)
        {
            Roots.Remove(entry);
        }

        internal void ReplaceRoots(IEnumerable<RootEntryViewModel> entries)
        {
            _suppressRootEvents = true;
            try
            {
                Roots.Clear();
                foreach (var entry in entries)
                {
                    Roots.Add(entry);
                }
            }
            finally
            {
                _suppressRootEvents = false;
            }

            _owner.RevalidateRoots(this);
        }

        private void OnRootsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (_suppressRootEvents)
            {
                return;
            }

            _owner.RevalidateRoots(this);
        }
    }

    public sealed partial class ServerSelectionViewModel : ObservableObject
    {
        internal const string DefaultRootPath = "C:\\Program Files";

        private static readonly string[] DefaultLabels =
        {
            "App Inc",
            "Supporting App",
            "FreakyFriday",
            "Auxiliary",
            "Helper",
            "Diagnostics",
        };

        private readonly IDriftbusterService _service;
        private readonly IToastService _toastService;
        private readonly ISessionCacheService _cacheService;
        private readonly Dictionary<string, RootValidationResult> _validationCache = new(StringComparer.OrdinalIgnoreCase);
        private CancellationTokenSource? _runCancellation;
        private ServerScanResponse? _lastResponse;
        private readonly ObservableCollection<ActivityEntryViewModel> _activityEntries = new();
        private readonly ReadOnlyObservableCollection<ActivityEntryViewModel> _activityEntriesReadonly;

        private const int MaxActivityItems = 200;

        public ServerSelectionViewModel(IDriftbusterService service, IToastService toastService, ISessionCacheService? cacheService = null)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
            _toastService = toastService ?? throw new ArgumentNullException(nameof(toastService));
            _cacheService = cacheService ?? new SessionCacheService();

            ScopeOptions = new ReadOnlyCollection<ScanScopeOption>(new[]
            {
                new ScanScopeOption(ServerScanScope.AllDrives, "All drives"),
                new ScanScopeOption(ServerScanScope.SingleDrive, "Single drive"),
                new ScanScopeOption(ServerScanScope.CustomRoots, "Custom roots"),
            });

            AddRootCommand = new RelayCommand<ServerSlotViewModel>(OnAddRoot, slot => slot is not null && slot.IsEnabled);
            RemoveRootCommand = new RelayCommand<RootEntryViewModel>(OnRemoveRoot, root => root is not null);
            RunAllCommand = new AsyncRelayCommand(RunAllAsync, () => !IsBusy && HasActiveServers);
            RunMissingCommand = new AsyncRelayCommand(RunMissingAsync, () => !IsBusy && HasActiveServers);
            CancelRunsCommand = new RelayCommand(OnCancelRuns, () => IsBusy);
            ClearHistoryCommand = new RelayCommand(OnClearHistory);
            SaveSessionCommand = new AsyncRelayCommand(SaveSessionAsync, () => PersistSessionState && !IsBusy);
            CopyActivityCommand = new RelayCommand<ActivityEntryViewModel>(OnCopyActivity, entry => entry is not null);

            Servers = new ObservableCollection<ServerSlotViewModel>(CreateDefaultServers());
            CatalogViewModel = new ResultsCatalogViewModel();
            CatalogViewModel.ReScanRequested += (_, hosts) => _ = RunScopedAsync(hosts);
            CatalogViewModel.DrilldownRequested += (_, entry) => LoadDrilldown(entry.ConfigId);
            CatalogViewModel.PropertyChanged += OnCatalogPropertyChanged;

            _activityEntriesReadonly = new ReadOnlyObservableCollection<ActivityEntryViewModel>(_activityEntries);
            FilteredActivityEntries = new ObservableCollection<ActivityEntryViewModel>();

            ShowSetupCommand = new RelayCommand(() =>
            {
                IsViewingCatalog = false;
                IsViewingDrilldown = false;
            });
            ShowCatalogCommand = new RelayCommand(() =>
            {
                IsViewingCatalog = true;
                IsViewingDrilldown = false;
            }, () => CatalogViewModel.HasEntries);
            ShowDrilldownCommand = new RelayCommand(() =>
            {
                if (DrilldownViewModel is not null)
                {
                    IsViewingCatalog = false;
                    IsViewingDrilldown = true;
                }
            }, () => DrilldownViewModel is not null);

            RefreshActivityFilter();
            _ = LoadSessionAsync();
        }

        public ObservableCollection<ServerSlotViewModel> Servers { get; }

        public IReadOnlyList<ScanScopeOption> ScopeOptions { get; }

        public IReadOnlyList<ActivityFilterOption> ActivityFilterOptions { get; } = new ReadOnlyCollection<ActivityFilterOption>(new[]
        {
            ActivityFilterOption.All,
            ActivityFilterOption.Errors,
            ActivityFilterOption.Warnings,
            ActivityFilterOption.Exports,
        });

        [ObservableProperty]
        private bool _persistSessionState;

        [ObservableProperty]
        private bool _isBusy;

        [ObservableProperty]
        private string _statusBanner = "Ready to plan a multi-server scan.";

        [ObservableProperty]
        private bool _isViewingCatalog;

        [ObservableProperty]
        private bool _isViewingDrilldown;

        [ObservableProperty]
        private ConfigDrilldownViewModel? _drilldownViewModel;

        public IRelayCommand<ServerSlotViewModel> AddRootCommand { get; }

        public IRelayCommand<RootEntryViewModel> RemoveRootCommand { get; }

        public IAsyncRelayCommand RunAllCommand { get; }

        public IAsyncRelayCommand RunMissingCommand { get; }

        public IRelayCommand CancelRunsCommand { get; }

        public IRelayCommand ClearHistoryCommand { get; }

        public IAsyncRelayCommand SaveSessionCommand { get; }

        public IRelayCommand ShowSetupCommand { get; }

        public IRelayCommand ShowCatalogCommand { get; }

        public IRelayCommand ShowDrilldownCommand { get; }

        public ResultsCatalogViewModel CatalogViewModel { get; }

        public ReadOnlyObservableCollection<ActivityEntryViewModel> ActivityEntries => _activityEntriesReadonly;

        public ObservableCollection<ActivityEntryViewModel> FilteredActivityEntries { get; }

        public bool HasActivityEntries => _activityEntries.Count > 0;

        [ObservableProperty]
        private ActivityFilterOption _activityFilter = ActivityFilterOption.All;

        public event EventHandler<string>? CopyActivityRequested;

        public IRelayCommand<ActivityEntryViewModel> CopyActivityCommand { get; }

        internal Func<ConfigDrilldownExportRequest, Task>? ExportCallback { get; set; }

        partial void OnDrilldownViewModelChanging(ConfigDrilldownViewModel? value)
        {
            var current = DrilldownViewModel;
            if (current is not null)
            {
                current.BackRequested -= OnDrilldownBackRequested;
                current.ReScanRequested -= OnDrilldownReScanRequested;
                current.ExportRequested -= OnDrilldownExportRequested;
                current.CopyJsonRequested -= OnDrilldownCopyJsonRequested;
            }
        }

        partial void OnDrilldownViewModelChanged(ConfigDrilldownViewModel? value)
        {
            if (value is not null)
            {
                value.BackRequested += OnDrilldownBackRequested;
                value.ReScanRequested += OnDrilldownReScanRequested;
                value.ExportRequested += OnDrilldownExportRequested;
                value.CopyJsonRequested += OnDrilldownCopyJsonRequested;
            }

            ShowDrilldownCommand.NotifyCanExecuteChanged();
            if (value is null)
            {
                IsViewingDrilldown = false;
            }
        }

        partial void OnActivityFilterChanged(ActivityFilterOption value) => RefreshActivityFilter();

        public bool HasActiveServers => Servers.Any(slot => slot.IsEnabled);

        public IEnumerable<ServerSlotViewModel> ActiveServers => Servers.Where(slot => slot.IsEnabled);

        internal void NotifyServerToggled(ServerSlotViewModel slot)
        {
            OnPropertyChanged(nameof(HasActiveServers));
            RunAllCommand.NotifyCanExecuteChanged();
            RunMissingCommand.NotifyCanExecuteChanged();
        }

        internal void NotifyScopeChanged(ServerSlotViewModel slot)
        {
            if (slot.Scope == ServerScanScope.CustomRoots && slot.Roots.Count == 0)
            {
                slot.EnsureDefaultRoot();
            }

            RevalidateRoots(slot);
        }

        internal void RevalidateRoots(ServerSlotViewModel slot)
        {
            foreach (var root in slot.Roots)
            {
                var result = ValidateRoot(slot, root);
                root.ValidationState = result.State;
                root.StatusMessage = result.Message;
            }

            slot.RefreshValidationSummary();
            RunAllCommand.NotifyCanExecuteChanged();
            RunMissingCommand.NotifyCanExecuteChanged();
        }

        private IEnumerable<ServerSlotViewModel> CreateDefaultServers()
        {
            for (var index = 0; index < DefaultLabels.Length; index++)
            {
                var label = DefaultLabels[index];
                var enabled = index < 3;
                yield return new ServerSlotViewModel(this, index, label, enabled);
            }
        }

        private async Task LoadSessionAsync()
        {
            try
            {
                var snapshot = await _cacheService.LoadAsync().ConfigureAwait(false);
                if (snapshot is null)
                {
                    return;
                }

                PersistSessionState = snapshot.PersistSession;

                if (snapshot.CatalogSort is not null)
                {
                    CatalogViewModel.RestoreSortDescriptor(new CatalogSortDescriptor(snapshot.CatalogSort.Column, snapshot.CatalogSort.Descending));
                }

                if (!string.IsNullOrWhiteSpace(snapshot.ActivityFilter) && Enum.TryParse<ActivityFilterOption>(snapshot.ActivityFilter, true, out var savedFilter))
                {
                    ActivityFilter = savedFilter;
                }

                _activityEntries.Clear();
                if (snapshot.Activities is { Count: > 0 })
                {
                    foreach (var activity in snapshot.Activities.OrderBy(entry => entry.Timestamp))
                    {
                        if (!Enum.TryParse(activity.Severity, true, out ActivitySeverity severity))
                        {
                            severity = ActivitySeverity.Info;
                        }

                        var category = Enum.TryParse<ActivityCategory>(activity.Category ?? nameof(ActivityCategory.General), true, out var parsedCategory)
                            ? parsedCategory
                            : ActivityCategory.General;
                        var entryVm = new ActivityEntryViewModel(severity, activity.Summary, activity.Detail ?? string.Empty, activity.Timestamp, category);
                        _activityEntries.Insert(0, entryVm);
                    }
                    RefreshActivityFilter();
                    OnPropertyChanged(nameof(HasActivityEntries));
                }

                foreach (var entry in snapshot.Servers)
                {
                    var server = Servers.FirstOrDefault(slot => string.Equals(slot.HostId, entry.HostId, StringComparison.OrdinalIgnoreCase));
                    if (server is null)
                    {
                        continue;
                    }

                    server.IsEnabled = entry.Enabled;
                    server.Label = entry.Label;
                    server.Scope = entry.Scope;

                    var roots = entry.Roots.Where(root => !string.IsNullOrWhiteSpace(root))
                        .Select(root => new RootEntryViewModel(root.Trim()));
                    server.ReplaceRoots(roots);
                    server.ResetStatus();
                }

                StatusBanner = "Loaded saved session.";
                ShowCatalogCommand.NotifyCanExecuteChanged();
                LogActivity(ActivitySeverity.Success, "Loaded saved session", $"Restored {snapshot.Servers.Count} servers.");
            }
            catch (Exception ex)
            {
                StatusBanner = $"Failed to load session: {ex.Message}";
                _toastService.Show(
                    "Session load failed",
                    ex.Message,
                    ToastLevel.Warning,
                    TimeSpan.FromSeconds(6),
                    new ToastAction("Copy details", () => CopyToClipboardAsync(ex.ToString())));
                LogActivity(ActivitySeverity.Error, "Failed to load session", ex.ToString());
            }
        }

        private void OnAddRoot(ServerSlotViewModel? slot)
        {
            if (slot is null)
            {
                return;
            }

            var candidate = (slot.NewRootPath ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(candidate))
            {
                slot.RootInputError = "Enter a root path.";
                LogActivity(ActivitySeverity.Warning, $"Ignored blank root for {slot.Label}.");
                return;
            }

            if (slot.Roots.Any(root => string.Equals(root.Path, candidate, StringComparison.OrdinalIgnoreCase)))
            {
                slot.RootInputError = "Root already added.";
                LogActivity(ActivitySeverity.Warning, $"Duplicate root '{candidate}' ignored for {slot.Label}.");
                return;
            }

            slot.RootInputError = null;
            var entry = new RootEntryViewModel(candidate);
            slot.AddRoot(entry);
            LogActivity(ActivitySeverity.Info, $"Added root '{candidate}' to {slot.Label}.");
            slot.NewRootPath = string.Empty;
            var result = ValidateRoot(slot, entry);
            entry.ValidationState = result.State;
            entry.StatusMessage = result.Message;
            slot.RefreshValidationSummary();
        }

        private void OnRemoveRoot(RootEntryViewModel? entry)
        {
            if (entry is null)
            {
                return;
            }

            var slot = Servers.FirstOrDefault(server => server.Roots.Contains(entry));
            if (slot is null)
            {
                return;
            }

            if (slot.Roots.Count <= 1)
            {
                entry.StatusMessage = "At least one root is required.";
                entry.ValidationState = RootValidationState.Invalid;
                LogActivity(ActivitySeverity.Warning, $"Skipped removing last root from {slot.Label}.");
                slot.RefreshValidationSummary();
                return;
            }

            slot.RemoveRoot(entry);
            RevalidateRoots(slot);
            LogActivity(ActivitySeverity.Info, $"Removed root '{entry.Path}' from {slot.Label}.");
        }

        private async Task RunAllAsync()
        {
            await ExecuteRunAsync(retryOnly: false);
        }

        private async Task RunMissingAsync()
        {
            await ExecuteRunAsync(retryOnly: true);
        }

        private async Task RunScopedAsync(IReadOnlyCollection<string>? hostIds)
        {
            if (hostIds is null || hostIds.Count == 0 || IsBusy)
            {
                return;
            }

            var wasDrilldown = IsViewingDrilldown;
            await ExecuteRunAsync(retryOnly: false, scopedHostIds: hostIds);
            if (wasDrilldown && DrilldownViewModel is not null)
            {
                LoadDrilldown(DrilldownViewModel.ConfigId);
            }
            else
            {
                IsViewingCatalog = CatalogViewModel.HasEntries;
            }
        }

        private async Task ExecuteRunAsync(bool retryOnly, IReadOnlyCollection<string>? scopedHostIds = null)
        {
            if (!HasActiveServers)
            {
                StatusBanner = "Enable at least one server to run.";
                return;
            }

            var plans = PreparePlans(retryOnly, scopedHostIds, out var cachedCount);
            if (plans.Count == 0)
            {
                StatusBanner = cachedCount > 0
                    ? "All active hosts already have cached results."
                    : "No servers ready to run.";
                LogActivity(ActivitySeverity.Info, "No servers queued", cachedCount > 0 ? "All hosts served from cache." : "Enable or configure servers before running.");
                return;
            }

            _runCancellation = new CancellationTokenSource();
            IsBusy = true;
            StatusBanner = retryOnly ? "Re-running missing hosts…" : "Running multi-server scan…";
            IsViewingCatalog = false;
            IsViewingDrilldown = false;
            LogActivity(ActivitySeverity.Info, retryOnly ? "Re-running missing hosts" : "Running multi-server scan", $"Hosts queued: {plans.Count}, cached reused: {cachedCount}.");

            try
            {
                var progress = new Progress<ScanProgress>(UpdateProgress);
                var response = await _service.RunServerScansAsync(plans, progress, _runCancellation.Token).ConfigureAwait(false);
                ApplyResults(response);
                StatusBanner = "Scan complete.";
                _toastService.Show(
                    "Multi-server scan complete",
                    $"Processed {plans.Count} host(s).",
                    ToastLevel.Success,
                    TimeSpan.FromSeconds(4));
                LogActivity(ActivitySeverity.Success, "Scan complete", $"Processed {plans.Count} host(s). Cached reused: {cachedCount}.");
            }
            catch (OperationCanceledException)
            {
                StatusBanner = "Scan cancelled.";
                _toastService.Show("Scan cancelled", "Active scan was cancelled.", ToastLevel.Info, TimeSpan.FromSeconds(3));
                LogActivity(ActivitySeverity.Info, "Scan cancelled.");
            }
            catch (Exception ex)
            {
                StatusBanner = $"Scan failed: {ex.Message}";
                _toastService.Show(
                    "Multi-server scan failed",
                    ex.Message,
                    ToastLevel.Error,
                    TimeSpan.FromSeconds(10),
                    new ToastAction("Copy details", () => CopyToClipboardAsync(ex.ToString())));
                LogActivity(ActivitySeverity.Error, "Scan failed", ex.ToString());
            }
            finally
            {
                IsBusy = false;
                _runCancellation?.Dispose();
                _runCancellation = null;
            }
        }

        private void OnCancelRuns()
        {
            _runCancellation?.Cancel();
        }

        private void OnClearHistory()
        {
            foreach (var server in Servers)
            {
                server.LastRunAt = null;
                server.ResetStatus();
            }

            if (PersistSessionState)
            {
                _cacheService.Clear();
            }

            StatusBanner = "Session history cleared.";
            _toastService.Show("History cleared", "Session cache removed for this view.", ToastLevel.Info, TimeSpan.FromSeconds(3));
            _activityEntries.Clear();
            FilteredActivityEntries.Clear();
            OnPropertyChanged(nameof(HasActivityEntries));
            LogActivity(ActivitySeverity.Info, "Cleared session history.");
            CatalogViewModel.Reset();
            ShowCatalogCommand.NotifyCanExecuteChanged();
            DrilldownViewModel = null;
            IsViewingCatalog = false;
            IsViewingDrilldown = false;
        }

        private async Task SaveSessionAsync()
        {
            if (!PersistSessionState)
            {
                StatusBanner = "Enable session persistence to save.";
                return;
            }

            try
            {
                var snapshot = new ServerSelectionCache
                {
                    PersistSession = true,
                    Servers = Servers.Select(server => new ServerSelectionCacheEntry
                    {
                        HostId = server.HostId,
                        Label = server.Label,
                        Enabled = server.IsEnabled,
                        Scope = server.Scope,
                        Roots = server.Roots.Select(root => root.Path).ToArray(),
                    }).ToList(),
                    Activities = _activityEntries
                        .Select(entry => new ActivityCacheEntry
                        {
                            Timestamp = entry.Timestamp,
                            Severity = entry.Severity.ToString(),
                            Summary = entry.Summary,
                            Detail = entry.Detail,
                            Category = entry.Category.ToString(),
                        })
                        .ToList(),
                    CatalogSort = new CatalogSortCache
                    {
                        Column = CatalogViewModel.SortDescriptor.ColumnKey,
                        Descending = CatalogViewModel.SortDescriptor.Descending,
                    },
                    ActivityFilter = ActivityFilter.ToString(),
                };

                await _cacheService.SaveAsync(snapshot).ConfigureAwait(false);
                StatusBanner = "Session saved.";
                LogActivity(ActivitySeverity.Success, "Session saved", $"Cached {snapshot.Servers.Count} servers.");
            }
            catch (Exception ex)
            {
                StatusBanner = $"Failed to save session: {ex.Message}";
                _toastService.Show(
                    "Session save failed",
                    ex.Message,
                    ToastLevel.Error,
                    TimeSpan.FromSeconds(6),
                    new ToastAction("Copy details", () => CopyToClipboardAsync(ex.ToString())));
                LogActivity(ActivitySeverity.Error, "Failed to save session", ex.ToString());
            }
        }

        private List<ServerScanPlan> PreparePlans(bool retryOnly, IReadOnlyCollection<string>? scopedHostIds, out int cachedCount)
        {
            var plans = new List<ServerScanPlan>();
            cachedCount = 0;

            foreach (var server in ActiveServers)
            {
                if (scopedHostIds is not null && scopedHostIds.Count > 0)
                {
                    var matchesHostId = scopedHostIds.Any(id => string.Equals(id, server.HostId, StringComparison.OrdinalIgnoreCase));
                    var matchesLabel = scopedHostIds.Any(id => string.Equals(id, server.Label, StringComparison.OrdinalIgnoreCase));
                    if (!matchesHostId && !matchesLabel)
                    {
                        continue;
                    }
                }

                if (retryOnly && server.HasCachedResult)
                {
                    server.MarkState(ServerScanStatus.Cached, "Using cached result", server.LastRunAt);
                    cachedCount++;
                    continue;
                }

                if (!HasValidRoots(server))
                {
                    server.MarkState(ServerScanStatus.Failed, "Invalid roots");
                    continue;
                }

                var plan = new ServerScanPlan
                {
                    HostId = server.HostId,
                    Label = server.Label,
                    Scope = server.Scope,
                    Roots = server.Scope == ServerScanScope.CustomRoots
                        ? server.Roots.Select(root => root.Path).ToArray()
                        : Array.Empty<string>(),
                    Baseline = new ServerScanBaselinePreference
                    {
                        IsPreferred = server.Index == 0,
                        Priority = server.Index,
                        Role = "auto",
                    },
                    Export = new ServerScanExportOptions
                    {
                        IncludeCatalog = true,
                        IncludeDrilldown = true,
                        IncludeDiffs = true,
                        IncludeSummary = true,
                    },
                    CachedAt = server.LastRunAt,
                };

                plans.Add(plan);
                server.MarkState(ServerScanStatus.Queued, "Queued");
            }

            return plans;
        }

        private void UpdateProgress(ScanProgress progress)
        {
            var server = Servers.FirstOrDefault(slot => string.Equals(slot.HostId, progress.HostId, StringComparison.OrdinalIgnoreCase));
            if (server is null)
            {
                return;
            }

            server.MarkState(progress.Status, progress.Message, progress.Timestamp);

            if (progress.Status == ServerScanStatus.Running)
            {
                LogActivity(ActivitySeverity.Info, $"Running {server.Label}", progress.Message);
            }
            else if (progress.Status == ServerScanStatus.Failed)
            {
                LogActivity(ActivitySeverity.Error, $"Failed {server.Label}", progress.Message);
            }
        }

        private void ApplyResults(ServerScanResponse? response)
        {
            _lastResponse = response;

            if (response?.Results is not null)
            {
                foreach (var result in response.Results)
                {
                    var server = Servers.FirstOrDefault(slot => string.Equals(slot.HostId, result.HostId, StringComparison.OrdinalIgnoreCase));
                    if (server is null)
                    {
                        continue;
                    }

                    var status = result.UsedCache ? ServerScanStatus.Cached : result.Status;
                    var message = string.IsNullOrWhiteSpace(result.Message) ? status.ToString() : result.Message;
                    if (result.Status == ServerScanStatus.Failed && result.Availability != ServerAvailabilityStatus.Unknown)
                    {
                        message = $"{message} ({result.Availability})";
                    }
                    server.MarkState(status, message, result.Timestamp);

                    var severity = status switch
                    {
                        ServerScanStatus.Succeeded => ActivitySeverity.Success,
                        ServerScanStatus.Cached => ActivitySeverity.Success,
                        ServerScanStatus.Failed => ActivitySeverity.Error,
                        ServerScanStatus.Skipped => ActivitySeverity.Warning,
                        _ => ActivitySeverity.Info,
                    };
                    LogActivity(severity, $"{server.Label}: {status}", message);
                }

                var attention = response.Results
                    .Where(result => result.Availability is ServerAvailabilityStatus.NotFound or ServerAvailabilityStatus.PermissionDenied or ServerAvailabilityStatus.Offline)
                    .ToList();
                if (attention.Count > 0)
                {
                    var summary = string.Join(", ", attention.Select(entry => $"{entry.Label}: {entry.Availability}"));
                    _toastService.Show(
                        "Hosts require attention",
                        summary,
                        ToastLevel.Warning,
                        TimeSpan.FromSeconds(8));
                    LogActivity(ActivitySeverity.Warning, "Hosts require attention", summary);
                }
            }

            CatalogViewModel.LoadFromResponse(response, ActiveServers.Count());
            ShowCatalogCommand.NotifyCanExecuteChanged();
            ShowDrilldownCommand.NotifyCanExecuteChanged();

            if (!IsViewingDrilldown)
            {
                IsViewingCatalog = CatalogViewModel.HasEntries;
            }
        }

        private void LoadDrilldown(string configId)
        {
            if (_lastResponse?.Drilldown is not { Length: > 0 })
            {
                StatusBanner = "Drilldown details unavailable for this scan.";
                return;
            }

            var detail = _lastResponse.Drilldown.FirstOrDefault(entry => string.Equals(entry.ConfigId, configId, StringComparison.OrdinalIgnoreCase));
            if (detail is null)
            {
                StatusBanner = $"No drilldown data for {configId}.";
                return;
            }

            DrilldownViewModel = new ConfigDrilldownViewModel(detail);
            IsViewingCatalog = false;
            IsViewingDrilldown = true;
            StatusBanner = $"Drilldown ready for {DrilldownViewModel.DisplayName}.";
        }

        private void OnDrilldownBackRequested(object? sender, EventArgs e)
        {
            IsViewingDrilldown = false;
            IsViewingCatalog = CatalogViewModel.HasEntries;
        }

        private void OnDrilldownReScanRequested(object? sender, IReadOnlyList<string> hosts)
        {
            if (hosts is null || hosts.Count == 0)
            {
                return;
            }

            LogActivity(ActivitySeverity.Info, "Requested targeted re-scan", string.Join(", ", hosts));
            _ = RunScopedAsync(hosts);
        }

        private void OnDrilldownCopyJsonRequested(object? sender, string payload)
        {
            if (string.IsNullOrWhiteSpace(payload))
            {
                return;
            }

            _ = CopyToClipboardAsync(payload);
            StatusBanner = "Sanitized drilldown JSON copied to clipboard.";
            _toastService.Show(
                "JSON copied",
                "Drilldown payload is available in the clipboard.",
                ToastLevel.Info,
                TimeSpan.FromSeconds(3));
            LogActivity(ActivitySeverity.Info, "Copied drilldown JSON", DrilldownViewModel?.DisplayName ?? string.Empty, ActivityCategory.Export);
        }

        private void OnDrilldownExportRequested(object? sender, ConfigDrilldownExportRequest request)
        {
            _ = HandleExportAsync(request);
        }

        private async Task HandleExportAsync(ConfigDrilldownExportRequest request)
        {
            if (ExportCallback is not null)
            {
                await ExportCallback(request).ConfigureAwait(false);
                StatusBanner = $"Exported {request.DisplayName} ({request.Format}).";
                LogActivity(ActivitySeverity.Success, $"Exported {request.DisplayName} ({request.Format})", request.Payload, ActivityCategory.Export);
                return;
            }

            var directory = Path.Combine("artifacts", "exports");
            Directory.CreateDirectory(directory);
            var safeName = SanitizeFileName(string.IsNullOrWhiteSpace(request.DisplayName) ? request.ConfigId : request.DisplayName);
            var extension = request.Format == ConfigDrilldownViewModel.ExportFormat.Html ? "html" : "json";
            var fileName = $"{safeName}-{DateTime.UtcNow:yyyyMMddHHmmss}.{extension}";
            var path = Path.Combine(directory, fileName);
            await File.WriteAllTextAsync(path, request.Payload).ConfigureAwait(false);
            StatusBanner = $"Exported {request.DisplayName} to {path}.";
            LogActivity(ActivitySeverity.Success, $"Exported {request.DisplayName}", path, ActivityCategory.Export);
        }

        private static string SanitizeFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return "export";
            }

            var invalid = Path.GetInvalidFileNameChars();
            var cleaned = new string(name.Select(ch => invalid.Contains(ch) ? '-' : ch).ToArray());
            return cleaned.Length == 0 ? "export" : cleaned;
        }

        private void OnCopyActivity(ActivityEntryViewModel? entry)
        {
            if (entry is null)
            {
                return;
            }

            CopyActivityRequested?.Invoke(this, entry.ClipboardText);
        }

        private void LogActivity(ActivitySeverity severity, string summary, string? detail = null, ActivityCategory category = ActivityCategory.General)
        {
            var entry = new ActivityEntryViewModel(severity, summary, detail ?? string.Empty, DateTimeOffset.UtcNow, category);
            _activityEntries.Insert(0, entry);
            while (_activityEntries.Count > MaxActivityItems)
            {
                _activityEntries.RemoveAt(_activityEntries.Count - 1);
            }

            RefreshActivityFilter();
            OnPropertyChanged(nameof(HasActivityEntries));
        }

        private void RefreshActivityFilter()
        {
            FilteredActivityEntries.Clear();

            foreach (var entry in _activityEntries)
            {
                var include = ActivityFilter switch
                {
                    ActivityFilterOption.Errors => entry.IsError,
                    ActivityFilterOption.Warnings => entry.IsWarning,
                    ActivityFilterOption.Exports => entry.IsExport,
                    _ => true,
                };

                if (!include)
                {
                    continue;
                }

                FilteredActivityEntries.Add(entry);
            }
        }

        private static async Task CopyToClipboardAsync(string content)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                return;
            }

            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime)
            {
                var clipboard = lifetime.MainWindow?.Clipboard;
                if (clipboard is not null)
                {
                    await clipboard.SetTextAsync(content).ConfigureAwait(false);
                }
            }
        }

        private bool HasValidRoots(ServerSlotViewModel slot)
        {
            if (slot.Scope != ServerScanScope.CustomRoots)
            {
                return true;
            }

            return slot.Roots.Count > 0 && slot.Roots.All(root => root.ValidationState == RootValidationState.Valid);
        }

        private RootValidationResult ValidateRoot(ServerSlotViewModel slot, RootEntryViewModel entry)
        {
            var trimmed = (entry.Path ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(trimmed))
            {
                return RootValidationResult.Invalid("Provide a root path.");
            }

            if (slot.Roots.Count(root => string.Equals(root.Path, trimmed, StringComparison.OrdinalIgnoreCase)) > 1)
            {
                return RootValidationResult.Invalid("Duplicate root for this host.");
            }

            if (slot.Scope != ServerScanScope.CustomRoots)
            {
                return RootValidationResult.Pending("Roots not used for this scope.");
            }

            if (_validationCache.TryGetValue(trimmed, out var cached))
            {
                return cached;
            }

            try
            {
                if (!PathIsAbsolute(trimmed))
                {
                    return RootValidationResult.Invalid("Path must be absolute.");
                }
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                return RootValidationResult.Invalid("Path contains invalid characters.");
            }

            var exists = Directory.Exists(trimmed) || File.Exists(trimmed);
            var result = exists
                ? RootValidationResult.Valid("Ready")
                : RootValidationResult.Invalid("Path not found.");

            if (result.State == RootValidationState.Valid)
            {
                _validationCache[trimmed] = result;
            }

            return result;
        }

        private static bool PathIsAbsolute(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }

            if (value.StartsWith("\\\\", StringComparison.Ordinal))
            {
                return true;
            }

            return Path.IsPathRooted(value);
        }

        partial void OnIsBusyChanged(bool value)
        {
            RunAllCommand.NotifyCanExecuteChanged();
            RunMissingCommand.NotifyCanExecuteChanged();
            CancelRunsCommand.NotifyCanExecuteChanged();
            SaveSessionCommand.NotifyCanExecuteChanged();
        }

        partial void OnPersistSessionStateChanged(bool value)
        {
            SaveSessionCommand.NotifyCanExecuteChanged();
        }

        private void OnCatalogPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (string.Equals(e.PropertyName, nameof(ResultsCatalogViewModel.HasEntries), StringComparison.Ordinal))
            {
                ShowCatalogCommand.NotifyCanExecuteChanged();
                if (!CatalogViewModel.HasEntries && IsViewingCatalog)
                {
                    IsViewingCatalog = false;
                }
            }
        }

        private sealed record RootValidationResult(RootValidationState State, string Message)
        {
            public static RootValidationResult Pending(string message) => new(RootValidationState.Pending, message);

            public static RootValidationResult Valid(string message) => new(RootValidationState.Valid, message);

            public static RootValidationResult Invalid(string message) => new(RootValidationState.Invalid, message);
        }
    }
}
