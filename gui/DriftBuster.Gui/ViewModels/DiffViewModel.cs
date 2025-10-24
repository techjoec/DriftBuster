using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using DriftBuster.Backend.Models;
using DriftBuster.Gui.Services;
using Microsoft.Extensions.Logging;

namespace DriftBuster.Gui.ViewModels
{
    public partial class DiffViewModel : ObservableObject
    {
        private const int MaxVersions = 5;

        private readonly IDriftbusterService _service;
        private readonly DiffPlannerMruStore _mruStore;
        private readonly Func<DateTimeOffset> _clock;
        private readonly ObservableCollection<DiffPlannerMruEntryView> _mruEntries = new();
        private readonly RelayCommand<DiffJsonViewMode> _selectJsonViewModeCommand;
        private readonly Task _initializationTask;
        private readonly ILogger<DiffViewModel> _logger;
        private static readonly EventId SanitizedFallbackEventId = new(2101, "DiffPlannerSanitizedFallback");
        private static readonly EventId RawPayloadRejectedEventId = new(2102, "DiffPlannerRawPayloadRejected");
        private bool _suppressMruSelection;
        private bool _updatingBaseline;

        public ObservableCollection<DiffInput> Inputs { get; } = new();
        public ObservableCollection<DiffComparisonView> Comparisons { get; } = new();

        public ReadOnlyObservableCollection<DiffPlannerMruEntryView> MruEntries { get; }

        public Task Initialization => _initializationTask;

        [ObservableProperty]
        private string? _errorMessage;

        [ObservableProperty]
        private string _rawJson = string.Empty;

        [ObservableProperty]
        private string _sanitizedJson = string.Empty;

        [ObservableProperty]
        private DiffJsonViewMode _jsonViewMode = DiffJsonViewMode.Raw;

        [ObservableProperty]
        private DiffPlannerMruEntryView? _selectedMruEntry;

        public IAsyncRelayCommand RunDiffCommand { get; }
        public IRelayCommand AddVersionCommand { get; }
        public IRelayCommand<DiffInput> RemoveVersionCommand { get; }
        public IRelayCommand<DiffJsonViewMode> SelectJsonViewModeCommand { get; }

        public bool IsBusy => RunDiffCommand.IsRunning;
        public bool HasError => !string.IsNullOrEmpty(ErrorMessage);
        public bool HasRawJson => !string.IsNullOrEmpty(RawJson);
        public bool HasSanitizedJson => !string.IsNullOrEmpty(SanitizedJson);
        public bool HasMruEntries => _mruEntries.Count > 0;
        public bool HasResult => Comparisons.Count > 0;
        public bool ShouldShowPlanHint => !HasResult;
        public bool IsSanitizedViewActive => JsonViewMode == DiffJsonViewMode.Sanitized && HasSanitizedJson;
        public bool IsRawViewActive => JsonViewMode == DiffJsonViewMode.Raw || !HasSanitizedJson;
        public string ActiveJson => IsSanitizedViewActive ? SanitizedJson : RawJson;
        public bool CanCopyActiveJson => HasSanitizedJson
            ? JsonViewMode == DiffJsonViewMode.Sanitized && !string.IsNullOrEmpty(SanitizedJson)
            : HasRawJson;
        public bool HasAnyJson => HasRawJson || HasSanitizedJson;

        public IReadOnlyList<JsonViewOption> JsonViewOptions { get; } = new[]
        {
            new JsonViewOption(DiffJsonViewMode.Sanitized, "Sanitized"),
            new JsonViewOption(DiffJsonViewMode.Raw, "Raw"),
        };

        public DiffViewModel(
            IDriftbusterService service,
            DiffPlannerMruStore? mruStore = null,
            Func<DateTimeOffset>? clock = null,
            ILogger<DiffViewModel>? logger = null)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
            _mruStore = mruStore ?? new DiffPlannerMruStore();
            _clock = clock ?? (() => DateTimeOffset.UtcNow);
            _logger = logger ?? new FileJsonLogger<DiffViewModel>(Path.Combine("artifacts", "logs", "diff-planner-telemetry.json"));

            MruEntries = new ReadOnlyObservableCollection<DiffPlannerMruEntryView>(_mruEntries);
            _mruEntries.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasMruEntries));

            RunDiffCommand = new AsyncRelayCommand(RunDiffAsync, CanRunDiff);
            AddVersionCommand = new RelayCommand(AddVersion, () => Inputs.Count < MaxVersions);
            RemoveVersionCommand = new RelayCommand<DiffInput>(RemoveVersion, input => input is not null && Inputs.Count > 2);
            _selectJsonViewModeCommand = new RelayCommand<DiffJsonViewMode>(SetJsonViewMode, CanSetJsonViewMode);
            SelectJsonViewModeCommand = _selectJsonViewModeCommand;

            RunDiffCommand.PropertyChanged += (_, args) =>
            {
                if (string.Equals(args.PropertyName, nameof(AsyncRelayCommand.IsRunning), StringComparison.Ordinal))
                {
                    OnPropertyChanged(nameof(IsBusy));
                    RunDiffCommand.NotifyCanExecuteChanged();
                }
            };

            Inputs.CollectionChanged += OnInputsChanged;
            Comparisons.CollectionChanged += (_, _) =>
            {
                OnPropertyChanged(nameof(HasResult));
                OnPropertyChanged(nameof(ShouldShowPlanHint));
            };

            _initializationTask = LoadMruEntriesAsync();
            InitializeInputs();
        }

        partial void OnErrorMessageChanged(string? value)
        {
            OnPropertyChanged(nameof(HasError));
        }

        partial void OnRawJsonChanged(string value)
        {
            OnPropertyChanged(nameof(HasRawJson));
            OnPropertyChanged(nameof(IsRawViewActive));
            OnPropertyChanged(nameof(ActiveJson));
            OnPropertyChanged(nameof(CanCopyActiveJson));
            OnPropertyChanged(nameof(HasAnyJson));
        }

        partial void OnSanitizedJsonChanged(string value)
        {
            OnPropertyChanged(nameof(HasSanitizedJson));
            OnPropertyChanged(nameof(IsSanitizedViewActive));
            OnPropertyChanged(nameof(ActiveJson));
            OnPropertyChanged(nameof(CanCopyActiveJson));
            OnPropertyChanged(nameof(HasAnyJson));
            _selectJsonViewModeCommand.NotifyCanExecuteChanged();

            if (!HasSanitizedJson && JsonViewMode == DiffJsonViewMode.Sanitized)
            {
                LogSanitizedFallback("sanitized_payload_missing");
                JsonViewMode = DiffJsonViewMode.Raw;
            }
        }

        partial void OnJsonViewModeChanged(DiffJsonViewMode value)
        {
            OnPropertyChanged(nameof(IsSanitizedViewActive));
            OnPropertyChanged(nameof(IsRawViewActive));
            OnPropertyChanged(nameof(ActiveJson));
            OnPropertyChanged(nameof(CanCopyActiveJson));
        }

        partial void OnSelectedMruEntryChanged(DiffPlannerMruEntryView? value)
        {
            if (_suppressMruSelection)
            {
                return;
            }

            ApplyMruEntry(value);
        }

        private void InitializeInputs()
        {
            Inputs.Add(new DiffInput(this));
            Inputs.Add(new DiffInput(this));
            Inputs[0].IsBaseline = true;
            UpdateValidation();
        }

        private void OnInputsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            EnsureBaselinePresence();
            UpdateValidation();
            RunDiffCommand.NotifyCanExecuteChanged();
            AddVersionCommand.NotifyCanExecuteChanged();
            RemoveVersionCommand.NotifyCanExecuteChanged();
        }

        private void AddVersion()
        {
            if (Inputs.Count >= MaxVersions)
            {
                return;
            }

            Inputs.Add(new DiffInput(this));
        }

        private void RemoveVersion(DiffInput? input)
        {
            if (input is null)
            {
                return;
            }

            if (Inputs.Count <= 2)
            {
                return;
            }

            Inputs.Remove(input);
            EnsureBaselinePresence();
        }

        private void EnsureBaselinePresence()
        {
            if (_updatingBaseline || Inputs.Count == 0)
            {
                return;
            }

            if (!Inputs.Any(entry => entry.IsBaseline))
            {
                SetBaseline(Inputs[0]);
            }
        }

        internal void OnBaselineChanged(DiffInput input, bool isBaseline)
        {
            if (_updatingBaseline)
            {
                return;
            }

            if (isBaseline)
            {
                SetBaseline(input);
                return;
            }

            if (!Inputs.Any(entry => entry.IsBaseline))
            {
                SetBaseline(input);
            }
        }

        private void SetBaseline(DiffInput candidate)
        {
            if (_updatingBaseline)
            {
                return;
            }

            _updatingBaseline = true;
            foreach (var entry in Inputs)
            {
                entry.IsBaseline = ReferenceEquals(entry, candidate);
            }
            _updatingBaseline = false;
            UpdateValidation();
        }

        private bool CanRunDiff()
        {
            if (RunDiffCommand.IsRunning)
            {
                return false;
            }
            var baseline = Inputs.FirstOrDefault(input => input.IsBaseline);
            if (baseline is null || string.IsNullOrWhiteSpace(baseline.Path) || !string.IsNullOrEmpty(baseline.Error))
            {
                return false;
            }

            var comparisonCount = Inputs
                .Where(input => !ReferenceEquals(input, baseline))
                .Count(input => !string.IsNullOrWhiteSpace(input.Path) && string.IsNullOrEmpty(input.Error));

            return comparisonCount >= 1;
        }

        private bool CanSetJsonViewMode(DiffJsonViewMode mode)
        {
            return mode != DiffJsonViewMode.Sanitized || HasSanitizedJson;
        }

        private void SetJsonViewMode(DiffJsonViewMode mode)
        {
            if (mode == DiffJsonViewMode.Sanitized && !HasSanitizedJson)
            {
                LogSanitizedFallback("sanitized_not_available");
                mode = DiffJsonViewMode.Raw;
            }

            JsonViewMode = mode;
        }

        public bool TryGetCopyPayload([NotNullWhen(true)] out string? payload)
        {
            payload = null;

            if (HasSanitizedJson && JsonViewMode != DiffJsonViewMode.Sanitized)
            {
                LogRawPayloadRejected("raw_blocked_sanitized_preferred");
                return false;
            }

            var candidate = ActiveJson;
            if (string.IsNullOrEmpty(candidate))
            {
                return false;
            }

            payload = candidate;
            return true;
        }

        private async Task RunDiffAsync()
        {
            ErrorMessage = null;

            try
            {
                var baseline = Inputs.FirstOrDefault(input => input.IsBaseline);
                if (baseline is null)
                {
                    ErrorMessage = "Select a baseline file.";
                    return;
                }

                if (string.IsNullOrWhiteSpace(baseline.Path) || !string.IsNullOrEmpty(baseline.Error))
                {
                    ErrorMessage = baseline.Error ?? "Select a baseline file.";
                    return;
                }

                var comparisons = Inputs
                    .Where(input => !ReferenceEquals(input, baseline))
                    .Where(input => !string.IsNullOrWhiteSpace(input.Path) && string.IsNullOrEmpty(input.Error))
                    .Select(input => input.Path!)
                    .ToList();

                if (comparisons.Count == 0)
                {
                    ErrorMessage = "Select at least one comparison file.";
                    return;
                }

                var orderedPaths = new List<string?> { baseline.Path };
                orderedPaths.AddRange(comparisons);

                var result = await _service.DiffAsync(orderedPaths).ConfigureAwait(false);
                await ApplyResultAsync(result, orderedPaths).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                ErrorMessage = ex.Message;
                Comparisons.Clear();
                RawJson = string.Empty;
                SanitizedJson = string.Empty;
                JsonViewMode = DiffJsonViewMode.Raw;
            }
        }

        private async Task ApplyResultAsync(DiffResult result, IReadOnlyList<string?> orderedPaths)
        {
            Comparisons.Clear();

            foreach (var comparison in result.Comparisons)
            {
                Comparisons.Add(new DiffComparisonView(comparison));
            }

            RawJson = result.RawJson ?? string.Empty;
            SanitizedJson = result.SanitizedJson ?? string.Empty;

            if (HasSanitizedJson)
            {
                JsonViewMode = DiffJsonViewMode.Sanitized;
            }
            else
            {
                JsonViewMode = DiffJsonViewMode.Raw;
            }

            await RecordMruEntryAsync(orderedPaths).ConfigureAwait(false);
        }

        private void UpdateValidation()
        {
            foreach (var input in Inputs)
            {
                var path = input.Path;
                if (string.IsNullOrWhiteSpace(path))
                {
                    input.Error = input.IsBaseline ? "Select a baseline file" : null;
                    continue;
                }

                input.Error = File.Exists(path) ? null : "File not found";
            }

            RunDiffCommand.NotifyCanExecuteChanged();
        }

        private async Task LoadMruEntriesAsync()
        {
            try
            {
                var snapshot = await _mruStore.LoadAsync().ConfigureAwait(false);
                var ordered = snapshot.Entries
                    .OrderByDescending(entry => entry.LastUsedUtc)
                    .Select(entry => new DiffPlannerMruEntryView(entry))
                    .ToList();

                _mruEntries.Clear();
                foreach (var entry in ordered)
                {
                    _mruEntries.Add(entry);
                }
            }
            catch
            {
                _mruEntries.Clear();
            }
        }

        private async Task RecordMruEntryAsync(IReadOnlyList<string?> orderedPaths)
        {
            if (orderedPaths.Count == 0)
            {
                return;
            }

            var baseline = orderedPaths[0];
            if (string.IsNullOrWhiteSpace(baseline))
            {
                return;
            }

            var comparisons = orderedPaths
                .Skip(1)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => path!.Trim())
                .ToList();

            if (comparisons.Count == 0)
            {
                return;
            }

            var entry = new DiffPlannerMruEntry
            {
                BaselinePath = baseline.Trim(),
                ComparisonPaths = comparisons,
                DisplayName = BuildDisplayName(baseline, comparisons),
                LastUsedUtc = _clock(),
                PayloadKind = DeterminePayloadKind(),
                SanitizedDigest = ComputeSanitizedDigest(),
            };

            try
            {
                await _mruStore.RecordAsync(entry).ConfigureAwait(false);
            }
            catch
            {
                return;
            }

            PromoteMruEntry(entry);
        }

        private DiffPlannerPayloadKind DeterminePayloadKind()
        {
            if (HasSanitizedJson)
            {
                return DiffPlannerPayloadKind.Sanitized;
            }

            return HasRawJson ? DiffPlannerPayloadKind.Raw : DiffPlannerPayloadKind.Unknown;
        }

        private string? ComputeSanitizedDigest()
        {
            if (!HasSanitizedJson)
            {
                return null;
            }

            using var sha256 = SHA256.Create();
            var data = Encoding.UTF8.GetBytes(SanitizedJson);
            var hash = sha256.ComputeHash(data);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        private static string BuildDisplayName(string baseline, IReadOnlyList<string> comparisons)
        {
            var baselineName = Path.GetFileName(baseline);
            var comparisonNames = comparisons
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToArray();

            var comparisonLabel = comparisonNames.Length == 0
                ? "(comparison)"
                : string.Join(", ", comparisonNames);

            return string.IsNullOrWhiteSpace(baselineName)
                ? comparisonLabel
                : $"{baselineName} vs {comparisonLabel}";
        }

        private void PromoteMruEntry(DiffPlannerMruEntry entry)
        {
            var existing = _mruEntries.FirstOrDefault(candidate => candidate.IsEquivalentTo(entry));
            if (existing is not null)
            {
                _mruEntries.Remove(existing);
            }

            var view = new DiffPlannerMruEntryView(entry);
            _mruEntries.Insert(0, view);

            _suppressMruSelection = true;
            try
            {
                SelectedMruEntry = view;
            }
            finally
            {
                _suppressMruSelection = false;
            }
        }

        private void ApplyMruEntry(DiffPlannerMruEntryView? view)
        {
            if (view is null)
            {
                return;
            }

            var required = Math.Max(2, Math.Min(MaxVersions, view.Entry.ComparisonPaths.Count + 1));
            EnsureInputCount(required);

            var baselineInput = Inputs[0];
            baselineInput.Path = view.Entry.BaselinePath;
            SetBaseline(baselineInput);

            for (var index = 0; index < view.Entry.ComparisonPaths.Count && index + 1 < Inputs.Count; index++)
            {
                Inputs[index + 1].Path = view.Entry.ComparisonPaths[index];
            }

            for (var index = view.Entry.ComparisonPaths.Count + 1; index < Inputs.Count; index++)
            {
                Inputs[index].Path = null;
            }

            UpdateValidation();

            if (view.Entry.PayloadKind == DiffPlannerPayloadKind.Sanitized && HasSanitizedJson)
            {
                JsonViewMode = DiffJsonViewMode.Sanitized;
            }
            else if (view.Entry.PayloadKind == DiffPlannerPayloadKind.Raw)
            {
                JsonViewMode = DiffJsonViewMode.Raw;
            }
        }

        private void EnsureInputCount(int required)
        {
            while (Inputs.Count < required)
            {
                Inputs.Add(new DiffInput(this));
            }

            while (Inputs.Count > required)
            {
                Inputs.RemoveAt(Inputs.Count - 1);
            }
        }

        public static string FormatSequence(IReadOnlyList<string>? values)
        {
            if (values is null || values.Count == 0)
            {
                return "—";
            }

            return string.Join(", ", values);
        }

        public static string SummariseText(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return "(empty)";
            }

            const int limit = 120;
            return text.Length <= limit ? text : text[..limit] + "…";
        }

        public sealed class DiffPlannerMruEntryView
        {
            public DiffPlannerMruEntryView(DiffPlannerMruEntry entry)
            {
                Entry = entry ?? throw new ArgumentNullException(nameof(entry));
                DisplayName = string.IsNullOrWhiteSpace(entry.DisplayName)
                    ? BuildDisplayName(entry.BaselinePath, entry.ComparisonPaths)
                    : entry.DisplayName!;
            }

            public DiffPlannerMruEntry Entry { get; }

            public string DisplayName { get; }

            public DiffPlannerPayloadKind PayloadKind => Entry.PayloadKind;

            public bool IsEquivalentTo(DiffPlannerMruEntry candidate)
            {
                if (candidate is null)
                {
                    return false;
                }

                if (!string.Equals(Entry.BaselinePath, candidate.BaselinePath, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                if (Entry.ComparisonPaths.Count != candidate.ComparisonPaths.Count)
                {
                    return false;
                }

                for (var index = 0; index < Entry.ComparisonPaths.Count; index++)
                {
                    if (!string.Equals(
                            Entry.ComparisonPaths[index],
                            candidate.ComparisonPaths[index],
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }
                }

                return true;
            }
        }

        public sealed class JsonViewOption
        {
            public JsonViewOption(DiffJsonViewMode mode, string displayName)
            {
                Mode = mode;
                DisplayName = displayName;
            }

            public DiffJsonViewMode Mode { get; }

            public string DisplayName { get; }
        }

        public enum DiffJsonViewMode
        {
            Sanitized = 0,
            Raw = 1,
        }

        public sealed partial class DiffInput : ObservableObject
        {
            public DiffInput(DiffViewModel owner)
            {
                Owner = owner;
            }

            public DiffViewModel Owner { get; }

            [ObservableProperty]
            private string? _path;

            [ObservableProperty]
            private string? _error;

            [ObservableProperty]
            private bool _isBaseline;

            partial void OnPathChanged(string? value)
            {
                Owner.UpdateValidation();
            }

            partial void OnIsBaselineChanged(bool value)
            {
                Owner.OnBaselineChanged(this, value);
            }
        }

        public sealed class DiffComparisonView
        {
            public DiffComparisonView(DiffComparison comparison)
            {
                Title = string.IsNullOrWhiteSpace(comparison.To)
                    ? comparison.From
                    : $"{comparison.From} → {comparison.To}";

                UnifiedDiff = comparison.UnifiedDiff ?? string.Empty;
                HasDiff = !string.IsNullOrWhiteSpace(UnifiedDiff);

                PlanEntries = new ObservableCollection<NameValuePair>(
                    new[]
                    {
                        new NameValuePair("Baseline label", comparison.Plan.FromLabel ?? comparison.From),
                        new NameValuePair("Comparison label", comparison.Plan.ToLabel ?? comparison.To),
                        new NameValuePair("Before summary", SummariseText(comparison.Plan.Before)),
                        new NameValuePair("After summary", SummariseText(comparison.Plan.After)),
                        new NameValuePair("Content type", comparison.Plan.ContentType),
                        new NameValuePair("Label", comparison.Plan.Label ?? "—"),
                        new NameValuePair("Mask tokens", FormatSequence(comparison.Plan.MaskTokens ?? Array.Empty<string>())),
                        new NameValuePair("Placeholder", comparison.Plan.Placeholder),
                        new NameValuePair("Context lines", comparison.Plan.ContextLines.ToString()),
                    });

                MetadataEntries = new ObservableCollection<NameValuePair>(
                    new[]
                    {
                        new NameValuePair("Baseline path", comparison.Metadata.LeftPath),
                        new NameValuePair("Comparison path", comparison.Metadata.RightPath),
                        new NameValuePair("Content type", comparison.Metadata.ContentType),
                        new NameValuePair("Context lines", comparison.Metadata.ContextLines.ToString()),
                    });
            }

            public string Title { get; }

            public ObservableCollection<NameValuePair> PlanEntries { get; }

            public ObservableCollection<NameValuePair> MetadataEntries { get; }

            public string UnifiedDiff { get; }

            public bool HasDiff { get; }
        }

        public sealed class NameValuePair
        {
            public NameValuePair(string name, string value)
            {
                Name = name;
                Value = value;
            }

            public string Name { get; }

            public string Value { get; }
        }

        private void LogSanitizedFallback(string reason)
        {
            var telemetry = CreateTelemetry(reason);
            _logger.Log(
                LogLevel.Information,
                SanitizedFallbackEventId,
                telemetry,
                null,
                static (DiffPlannerPayloadRejectTelemetry state, Exception? _) =>
                    $"Sanitized payload unavailable. reason={state.RejectReason}");
        }

        private void LogRawPayloadRejected(string reason)
        {
            var telemetry = CreateTelemetry(reason);
            _logger.Log(
                LogLevel.Information,
                RawPayloadRejectedEventId,
                telemetry,
                null,
                static (DiffPlannerPayloadRejectTelemetry state, Exception? _) =>
                    $"Raw payload rejected. reason={state.RejectReason}");
        }

        private DiffPlannerPayloadRejectTelemetry CreateTelemetry(string reason)
        {
            return new DiffPlannerPayloadRejectTelemetry
            {
                RejectReason = reason,
                ActiveMode = JsonViewMode.ToString(),
                HasSanitizedJson = HasSanitizedJson,
                HasRawJson = HasRawJson,
                ComparisonCount = Comparisons.Count,
                RawLength = RawJson?.Length ?? 0,
                SanitizedLength = SanitizedJson?.Length ?? 0,
            };
        }

        private sealed class DiffPlannerPayloadRejectTelemetry
        {
            public string RejectReason { get; init; } = string.Empty;

            public string ActiveMode { get; init; } = string.Empty;

            public bool HasSanitizedJson { get; init; }

            public bool HasRawJson { get; init; }

            public int ComparisonCount { get; init; }

            public int RawLength { get; init; }

            public int SanitizedLength { get; init; }
        }
    }
}
