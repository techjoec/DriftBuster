using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using DriftBuster.Gui.Models;
using DriftBuster.Gui.Services;

namespace DriftBuster.Gui.ViewModels
{
    public partial class DiffViewModel : ObservableObject
    {
        private const int MaxVersions = 5;

        private readonly IDriftbusterService _service;
        private bool _updatingBaseline;

        public ObservableCollection<DiffInput> Inputs { get; } = new();
        public ObservableCollection<DiffComparisonView> Comparisons { get; } = new();

        [ObservableProperty]
        private string? _errorMessage;

        [ObservableProperty]
        private string _rawJson = string.Empty;

        public IAsyncRelayCommand RunDiffCommand { get; }
        public IRelayCommand AddVersionCommand { get; }
        public IRelayCommand<DiffInput> RemoveVersionCommand { get; }

        public bool IsBusy => RunDiffCommand.IsRunning;
        public bool HasError => !string.IsNullOrEmpty(ErrorMessage);
        public bool HasRawJson => !string.IsNullOrEmpty(RawJson);
        public bool HasResult => Comparisons.Count > 0;
        public bool ShouldShowPlanHint => !HasResult;

        public DiffViewModel(IDriftbusterService service)
        {
            _service = service;

            RunDiffCommand = new AsyncRelayCommand(RunDiffAsync, CanRunDiff);
            AddVersionCommand = new RelayCommand(AddVersion, () => Inputs.Count < MaxVersions);
            RemoveVersionCommand = new RelayCommand<DiffInput>(RemoveVersion, input => input is not null && Inputs.Count > 2);

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

            InitializeInputs();
        }

        partial void OnErrorMessageChanged(string? value)
        {
            OnPropertyChanged(nameof(HasError));
        }

        partial void OnRawJsonChanged(string value)
        {
            OnPropertyChanged(nameof(HasRawJson));
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
                ApplyResult(result);
            }
            catch (Exception ex)
            {
                ErrorMessage = ex.Message;
                Comparisons.Clear();
                RawJson = string.Empty;
            }
        }

        private void ApplyResult(DiffResult result)
        {
            Comparisons.Clear();

            foreach (var comparison in result.Comparisons)
            {
                Comparisons.Add(new DiffComparisonView(comparison));
            }

            RawJson = result.RawJson;
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
    }
}
