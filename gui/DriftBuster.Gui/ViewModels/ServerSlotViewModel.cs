using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;

using CommunityToolkit.Mvvm.ComponentModel;

using DriftBuster.Backend.Models;
using DriftBuster.Gui.Services;

namespace DriftBuster.Gui.ViewModels
{
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

        private int _index;

        public int Index
        {
            get => _index;
            internal set => SetProperty(ref _index, value);
        }

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
            DebugLog.Trace("ServerSlot", "OnIsEnabledChanged", new { IsEnabled = value, Label, Index });

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
            DebugLog.Trace("ServerSlot", "OnScopeChanged", new { NewScope = value.ToString(), Label });
            _owner.NotifyScopeChanged(this);
        }

        public void MarkState(ServerScanStatus state, string statusText, DateTimeOffset? timestamp = null)
        {
            DebugLog.Trace("ServerSlot", "MarkState", new { State = state.ToString(), StatusText = statusText, Label, Timestamp = timestamp });
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
            DebugLog.Trace("ServerSlot", "RefreshValidationSummary", new { Summary = ValidationSummary, RootCount = Roots.Count, Label });
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
                .Distinct(StringComparer.Ordinal)
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
            DebugLog.Trace("ServerSlot", "EnsureDefaultRoot", new { CurrentRootCount = Roots.Count, Label });

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
            DebugLog.Trace("ServerSlot", "AddRoot", new { entry.Path, Label, CountAfter = Roots.Count + 1 });
            Roots.Add(entry);
        }

        internal void RemoveRoot(RootEntryViewModel entry)
        {
            DebugLog.Trace("ServerSlot", "RemoveRoot", new { entry.Path, Label, CountAfter = Roots.Count - 1 });
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

            if (DebugLog.IsEnabled)
            {
                DebugLog.Trace("ServerSlot", "ReplaceRoots", new { Count = Roots.Count, Paths = Roots.Select(r => r.Path).ToArray(), Label });
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
}
