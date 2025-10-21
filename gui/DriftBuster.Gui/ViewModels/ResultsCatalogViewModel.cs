using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using DriftBuster.Backend.Models;

namespace DriftBuster.Gui.ViewModels
{
    public enum CoverageFilterOption
    {
        All,
        Full,
        Partial,
        Missing,
    }

    public enum SeverityFilterOption
    {
        Any,
        High,
        Medium,
        Low,
        None,
    }

    public sealed class FilterOption<T>
    {
        public FilterOption(T value, string display)
        {
            Value = value;
            Display = display;
        }

        public T Value { get; }

        public string Display { get; }

        public override string ToString() => Display;
    }

    public sealed partial class ConfigCatalogItemViewModel : ObservableObject
    {
        private readonly ConfigCatalogEntry _entry;

        public ConfigCatalogItemViewModel(ConfigCatalogEntry entry, int totalHosts)
        {
            _entry = entry ?? throw new ArgumentNullException(nameof(entry));
            TotalHosts = Math.Max(totalHosts, PresentHosts.Count + MissingHosts.Count);
            if (TotalHosts == 0)
            {
                TotalHosts = Math.Max(1, PresentHosts.Count + MissingHosts.Count);
            }
        }

        public string ConfigId => _entry.ConfigId;

        public string DisplayName => string.IsNullOrWhiteSpace(_entry.DisplayName) ? ConfigId : _entry.DisplayName;

        public string Format => string.IsNullOrWhiteSpace(_entry.Format) ? "unknown" : _entry.Format;

        public int DriftCount => _entry.DriftCount;

        public string Severity => string.IsNullOrWhiteSpace(_entry.Severity) ? "none" : _entry.Severity;

        public IReadOnlyList<string> PresentHosts => _entry.PresentHosts ?? Array.Empty<string>();

        public IReadOnlyList<string> MissingHosts => _entry.MissingHosts ?? Array.Empty<string>();

        public int PresentCount => PresentHosts.Count;

        public int TotalHosts { get; }

        public string CoverageText => TotalHosts <= 0 ? "0/0" : $"{PresentCount}/{TotalHosts}";

        public string CoverageStatus => string.IsNullOrWhiteSpace(_entry.CoverageStatus) ? (IsFullCoverage ? "full" : IsMissingCoverage ? "missing" : "partial") : _entry.CoverageStatus;

        public bool HasDrift => DriftCount > 0;

        public bool HasSecrets => _entry.HasSecrets;

        public bool HasMaskedTokens => _entry.HasMaskedTokens;

        public bool HasValidationIssues => _entry.HasValidationIssues;

        public bool IsFullCoverage => PresentCount >= TotalHosts && TotalHosts > 0;

        public bool IsPartialCoverage => !IsFullCoverage && PresentCount > 0;

        public bool IsMissingCoverage => PresentCount == 0;

        public string DriftSummary => DriftCount > 0 ? $"{DriftCount}" : "—";

        public DateTimeOffset LastUpdated => _entry.LastUpdated;

        public string LastUpdatedText => LastUpdated.ToLocalTime().ToString("g");

        public bool HasAnyAlerts => HasSecrets || HasValidationIssues || HasMaskedTokens || DriftCount > 0;

        public string MissingHostsSummary => MissingHosts.Count == 0 ? "" : string.Join(", ", MissingHosts);
    }

    public sealed partial class ResultsCatalogViewModel : ObservableObject
    {
        private const string DefaultFormatOption = "Any";

        public ResultsCatalogViewModel()
        {
            Entries = new ObservableCollection<ConfigCatalogItemViewModel>();
            FilteredEntries = new ObservableCollection<ConfigCatalogItemViewModel>();

            CoverageFilterOptions = new ReadOnlyCollection<FilterOption<CoverageFilterOption>>(new[]
            {
                new FilterOption<CoverageFilterOption>(CoverageFilterOption.All, "All coverage"),
                new FilterOption<CoverageFilterOption>(CoverageFilterOption.Full, "Full coverage"),
                new FilterOption<CoverageFilterOption>(CoverageFilterOption.Partial, "Partial"),
                new FilterOption<CoverageFilterOption>(CoverageFilterOption.Missing, "Missing"),
            });

            SeverityFilterOptions = new ReadOnlyCollection<FilterOption<SeverityFilterOption>>(new[]
            {
                new FilterOption<SeverityFilterOption>(SeverityFilterOption.Any, "Any severity"),
                new FilterOption<SeverityFilterOption>(SeverityFilterOption.High, "High"),
                new FilterOption<SeverityFilterOption>(SeverityFilterOption.Medium, "Medium"),
                new FilterOption<SeverityFilterOption>(SeverityFilterOption.Low, "Low"),
                new FilterOption<SeverityFilterOption>(SeverityFilterOption.None, "None"),
            });

            BaselineOptions = new ReadOnlyCollection<string>(new[] { "Match", "Drift" });
            FormatOptions = new ObservableCollection<string> { DefaultFormatOption };

            SelectedCoverageFilter = CoverageFilterOption.All;
            SelectedSeverityFilter = SeverityFilterOption.Any;
            SelectedFormat = DefaultFormatOption;
            SelectedBaseline = BaselineOptions[0];

            DrilldownCommand = new RelayCommand<ConfigCatalogItemViewModel>(OnDrilldownRequested, entry => entry is not null);
            ReScanMissingCommand = new RelayCommand<ConfigCatalogItemViewModel>(OnReScanMissingRequested, entry => entry is not null && entry.MissingHosts.Count > 0);
            ReScanAllPartialCommand = new RelayCommand(OnReScanAllPartial, () => PartialCoverageEntries.Any());

            ApplyFilters();
        }

        public ObservableCollection<ConfigCatalogItemViewModel> Entries { get; }

        public ObservableCollection<ConfigCatalogItemViewModel> FilteredEntries { get; }

        public ReadOnlyCollection<FilterOption<CoverageFilterOption>> CoverageFilterOptions { get; }

        public ReadOnlyCollection<FilterOption<SeverityFilterOption>> SeverityFilterOptions { get; }

        public ReadOnlyCollection<string> BaselineOptions { get; }

        public ObservableCollection<string> FormatOptions { get; }

        [ObservableProperty]
        private CoverageFilterOption _selectedCoverageFilter;

        partial void OnSelectedCoverageFilterChanged(CoverageFilterOption value)
        {
            RefreshFilters();
        }

        [ObservableProperty]
        private SeverityFilterOption _selectedSeverityFilter;

        partial void OnSelectedSeverityFilterChanged(SeverityFilterOption value)
        {
            RefreshFilters();
        }

        [ObservableProperty]
        private string _selectedFormat = DefaultFormatOption;

        partial void OnSelectedFormatChanged(string value)
        {
            RefreshFilters();
        }

        [ObservableProperty]
        private string _selectedBaseline;

        [ObservableProperty]
        private string _searchText = string.Empty;

        partial void OnSearchTextChanged(string value)
        {
            RefreshFilters();
        }

        public bool HasEntries => Entries.Count > 0;

        public IEnumerable<ConfigCatalogItemViewModel> PartialCoverageEntries => Entries.Where(entry => entry.IsPartialCoverage || entry.IsMissingCoverage);

        public bool HasPartialCoverage => PartialCoverageEntries.Any();

        public IRelayCommand<ConfigCatalogItemViewModel> DrilldownCommand { get; }

        public IRelayCommand<ConfigCatalogItemViewModel> ReScanMissingCommand { get; }

        public IRelayCommand ReScanAllPartialCommand { get; }

        public event EventHandler<ConfigCatalogItemViewModel>? DrilldownRequested;

        public event EventHandler<IReadOnlyList<string>>? ReScanRequested;

        public void LoadFromResponse(ServerScanResponse? response, int totalHosts)
        {
            Entries.Clear();
            if (response?.Catalog is { Length: > 0 })
            {
                foreach (var entry in response.Catalog)
                {
                    Entries.Add(new ConfigCatalogItemViewModel(entry, totalHosts));
                }
            }

            UpdateFormatOptions();
            ApplyFilters();
            OnPropertyChanged(nameof(HasEntries));
        }

        public void Reset()
        {
            Entries.Clear();
            SelectedFormat = DefaultFormatOption;
            SearchText = string.Empty;
            SelectedCoverageFilter = CoverageFilterOption.All;
            SelectedSeverityFilter = SeverityFilterOption.Any;
            UpdateFormatOptions();
            ApplyFilters();
            OnPropertyChanged(nameof(HasEntries));
        }

        private void UpdateFormatOptions()
        {
            var previous = SelectedFormat;
            FormatOptions.Clear();
            FormatOptions.Add(DefaultFormatOption);

            var formats = Entries
                .Select(entry => entry.Format)
                .Where(format => !string.IsNullOrWhiteSpace(format))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(format => format, StringComparer.OrdinalIgnoreCase);

            foreach (var format in formats)
            {
                FormatOptions.Add(format);
            }

            SelectedFormat = FormatOptions.Contains(previous) ? previous : DefaultFormatOption;
        }

        private bool MatchesFilters(ConfigCatalogItemViewModel entry)
        {
            if (SelectedCoverageFilter == CoverageFilterOption.Full && !entry.IsFullCoverage)
            {
                return false;
            }

            if (SelectedCoverageFilter == CoverageFilterOption.Partial && !entry.IsPartialCoverage)
            {
                return false;
            }

            if (SelectedCoverageFilter == CoverageFilterOption.Missing && !entry.IsMissingCoverage)
            {
                return false;
            }

            if (SelectedSeverityFilter != SeverityFilterOption.Any)
            {
                var severity = entry.Severity.ToLowerInvariant();
                var required = SelectedSeverityFilter.ToString().ToLowerInvariant();
                if (!string.Equals(severity, required, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            if (!string.Equals(SelectedFormat, DefaultFormatOption, StringComparison.OrdinalIgnoreCase))
            {
                if (!string.Equals(entry.Format, SelectedFormat, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            if (!string.IsNullOrWhiteSpace(SearchText))
            {
                var query = SearchText.Trim();
                if (!entry.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                    !entry.ConfigId.Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            return true;
        }

        private void RefreshFilters()
        {
            ApplyFilters();
        }

        private void OnDrilldownRequested(ConfigCatalogItemViewModel? entry)
        {
            if (entry is null)
            {
                return;
            }

            DrilldownRequested?.Invoke(this, entry);
        }

        private void OnReScanMissingRequested(ConfigCatalogItemViewModel? entry)
        {
            if (entry is null)
            {
                return;
            }

            if (entry.MissingHosts.Count == 0)
            {
                return;
            }

            ReScanRequested?.Invoke(this, entry.MissingHosts.ToArray());
        }

        private void OnReScanAllPartial()
        {
            var hosts = PartialCoverageEntries
                .SelectMany(entry => entry.MissingHosts)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (hosts.Length == 0)
            {
                return;
            }

            ReScanRequested?.Invoke(this, hosts);
        }

        private void ApplyFilters()
        {
            var filtered = Entries.Where(MatchesFilters).ToList();

            FilteredEntries.Clear();
            foreach (var entry in filtered)
            {
                FilteredEntries.Add(entry);
            }

            OnPropertyChanged(nameof(HasPartialCoverage));
            OnPropertyChanged(nameof(PartialCoverageEntries));
            ReScanAllPartialCommand.NotifyCanExecuteChanged();
        }
    }
}
