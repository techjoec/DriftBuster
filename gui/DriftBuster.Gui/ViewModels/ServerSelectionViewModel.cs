using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

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

        public bool CanRetry => RunState == ServerScanStatus.Failed;

        public bool HasCachedResult => RunState is ServerScanStatus.Cached or ServerScanStatus.Succeeded;

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
        private readonly ISessionCacheService _cacheService;
        private readonly Dictionary<string, RootValidationResult> _validationCache = new(StringComparer.OrdinalIgnoreCase);
        private CancellationTokenSource? _runCancellation;

        public ServerSelectionViewModel(IDriftbusterService service, ISessionCacheService? cacheService = null)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
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

            Servers = new ObservableCollection<ServerSlotViewModel>(CreateDefaultServers());
            CatalogViewModel = new ResultsCatalogViewModel();
            CatalogViewModel.ReScanRequested += (_, hosts) => _ = RunScopedAsync(hosts);
            CatalogViewModel.DrilldownRequested += (_, entry) => StatusBanner = $"Drilldown ready for {entry.DisplayName}.";
            CatalogViewModel.PropertyChanged += OnCatalogPropertyChanged;

            ShowSetupCommand = new RelayCommand(() => IsViewingCatalog = false);
            ShowCatalogCommand = new RelayCommand(() => IsViewingCatalog = true, () => CatalogViewModel.HasEntries);

            _ = LoadSessionAsync();
        }

        public ObservableCollection<ServerSlotViewModel> Servers { get; }

        public IReadOnlyList<ScanScopeOption> ScopeOptions { get; }

        [ObservableProperty]
        private bool _persistSessionState;

        [ObservableProperty]
        private bool _isBusy;

        [ObservableProperty]
        private string _statusBanner = "Ready to plan a multi-server scan.";

        [ObservableProperty]
        private bool _isViewingCatalog;

        public IRelayCommand<ServerSlotViewModel> AddRootCommand { get; }

        public IRelayCommand<RootEntryViewModel> RemoveRootCommand { get; }

        public IAsyncRelayCommand RunAllCommand { get; }

        public IAsyncRelayCommand RunMissingCommand { get; }

        public IRelayCommand CancelRunsCommand { get; }

        public IRelayCommand ClearHistoryCommand { get; }

        public IAsyncRelayCommand SaveSessionCommand { get; }

        public IRelayCommand ShowSetupCommand { get; }

        public IRelayCommand ShowCatalogCommand { get; }

        public ResultsCatalogViewModel CatalogViewModel { get; }

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
            }
            catch (Exception ex)
            {
                StatusBanner = $"Failed to load session: {ex.Message}";
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
                return;
            }

            if (slot.Roots.Any(root => string.Equals(root.Path, candidate, StringComparison.OrdinalIgnoreCase)))
            {
                slot.RootInputError = "Root already added.";
                return;
            }

            slot.RootInputError = null;
            var entry = new RootEntryViewModel(candidate);
            slot.AddRoot(entry);
            slot.NewRootPath = string.Empty;
            var result = ValidateRoot(slot, entry);
            entry.ValidationState = result.State;
            entry.StatusMessage = result.Message;
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
                return;
            }

            slot.RemoveRoot(entry);
            RevalidateRoots(slot);
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

            await ExecuteRunAsync(retryOnly: false, scopedHostIds: hostIds);
            IsViewingCatalog = CatalogViewModel.HasEntries;
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
                return;
            }

            _runCancellation = new CancellationTokenSource();
            IsBusy = true;
            StatusBanner = retryOnly ? "Re-running missing hosts…" : "Running multi-server scan…";
            IsViewingCatalog = false;

            try
            {
                var progress = new Progress<ScanProgress>(UpdateProgress);
                var response = await _service.RunServerScansAsync(plans, progress, _runCancellation.Token).ConfigureAwait(false);
                ApplyResults(response);
                StatusBanner = "Scan complete.";
            }
            catch (OperationCanceledException)
            {
                StatusBanner = "Scan cancelled.";
            }
            catch (Exception ex)
            {
                StatusBanner = $"Scan failed: {ex.Message}";
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
            CatalogViewModel.Reset();
            ShowCatalogCommand.NotifyCanExecuteChanged();
            IsViewingCatalog = false;
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
                };

                await _cacheService.SaveAsync(snapshot).ConfigureAwait(false);
                StatusBanner = "Session saved.";
            }
            catch (Exception ex)
            {
                StatusBanner = $"Failed to save session: {ex.Message}";
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
        }

        private void ApplyResults(ServerScanResponse? response)
        {
            if (response?.Results is null)
            {
                return;
            }

            foreach (var result in response.Results)
            {
                var server = Servers.FirstOrDefault(slot => string.Equals(slot.HostId, result.HostId, StringComparison.OrdinalIgnoreCase));
                if (server is null)
                {
                    continue;
                }

                var status = result.UsedCache ? ServerScanStatus.Cached : result.Status;
                var message = string.IsNullOrWhiteSpace(result.Message) ? status.ToString() : result.Message;
                server.MarkState(status, message, result.Timestamp);
            }

            CatalogViewModel.LoadFromResponse(response, ActiveServers.Count());
            ShowCatalogCommand.NotifyCanExecuteChanged();
            IsViewingCatalog = CatalogViewModel.HasEntries;
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
