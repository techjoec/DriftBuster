using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using DriftBuster.Backend.Models;
using DriftBuster.Gui.Services;

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

        public int SeverityRank => Severity.ToLowerInvariant() switch
        {
            "high" => 0,
            "medium" => 1,
            "low" => 2,
            "none" => 3,
            _ => 4,
        };

        public IReadOnlyList<string> PresentHosts => _entry.PresentHosts ?? Array.Empty<string>();

        public IReadOnlyList<string> MissingHosts => _entry.MissingHosts ?? Array.Empty<string>();

        public int PresentCount => PresentHosts.Count;

        public int TotalHosts { get; }

        public string CoverageText => TotalHosts <= 0 ? "0/0" : $"{PresentCount}/{TotalHosts}";

        public double CoverageRatio => TotalHosts <= 0 ? 0d : (double)PresentCount / TotalHosts;

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

    public static class CatalogSortColumns
    {
        public const string Config = nameof(ConfigCatalogItemViewModel.DisplayName);
        public const string Drift = nameof(ConfigCatalogItemViewModel.DriftCount);
        public const string Coverage = nameof(ConfigCatalogItemViewModel.CoverageRatio);
        public const string Format = nameof(ConfigCatalogItemViewModel.Format);
        public const string Severity = nameof(ConfigCatalogItemViewModel.SeverityRank);
        public const string Updated = nameof(ConfigCatalogItemViewModel.LastUpdated);
    }

    public sealed record CatalogSortDescriptor(string ColumnKey, bool Descending)
    {
        public static CatalogSortDescriptor Default { get; } = new(CatalogSortColumns.Updated, true);

        public static CatalogSortDescriptor Normalize(string? columnKey, bool descending)
        {
            return columnKey?.Trim() switch
            {
                CatalogSortColumns.Drift => new CatalogSortDescriptor(CatalogSortColumns.Drift, descending),
                CatalogSortColumns.Coverage => new CatalogSortDescriptor(CatalogSortColumns.Coverage, descending),
                CatalogSortColumns.Format => new CatalogSortDescriptor(CatalogSortColumns.Format, descending),
                CatalogSortColumns.Severity => new CatalogSortDescriptor(CatalogSortColumns.Severity, descending),
                CatalogSortColumns.Updated => new CatalogSortDescriptor(CatalogSortColumns.Updated, descending),
                CatalogSortColumns.Config => new CatalogSortDescriptor(CatalogSortColumns.Config, descending),
                _ => new CatalogSortDescriptor(CatalogSortColumns.Config, descending),
            };
        }
    }

    public sealed partial class ResultsCatalogViewModel : ObservableObject
    {
        private const string DefaultFormatOption = "Any";

        private readonly PerformanceProfile _performanceProfile;

        public ResultsCatalogViewModel(PerformanceProfile? performanceProfile = null)
        {
            _performanceProfile = performanceProfile ?? PerformanceProfile.FromEnvironment();
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
            RefreshPartialCoverageVirtualization();
        }

        public ObservableCollection<ConfigCatalogItemViewModel> Entries { get; }

        public ObservableCollection<ConfigCatalogItemViewModel> FilteredEntries { get; }

        public ReadOnlyCollection<FilterOption<CoverageFilterOption>> CoverageFilterOptions { get; }

        public ReadOnlyCollection<FilterOption<SeverityFilterOption>> SeverityFilterOptions { get; }

        public ReadOnlyCollection<string> BaselineOptions { get; }

        public ObservableCollection<string> FormatOptions { get; }

        private CatalogSortDescriptor _sortDescriptor = CatalogSortDescriptor.Default;

        public CatalogSortDescriptor SortDescriptor => _sortDescriptor;

        public event EventHandler<CatalogSortDescriptor>? SortDescriptorChanged;

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

        [ObservableProperty]
        private bool _useVirtualizedPartialCoverage;

        public IRelayCommand<ConfigCatalogItemViewModel> DrilldownCommand { get; }

        public IRelayCommand<ConfigCatalogItemViewModel> ReScanMissingCommand { get; }

        public IRelayCommand ReScanAllPartialCommand { get; }

        public event EventHandler<ConfigCatalogItemViewModel>? DrilldownRequested;

        public event EventHandler<IReadOnlyList<string>>? ReScanRequested;

        public void SetSortDescriptor(string? columnKey, bool descending)
        {
            UpdateSortDescriptor(CatalogSortDescriptor.Normalize(columnKey, descending), raiseEvent: true);
        }

        public void RestoreSortDescriptor(CatalogSortDescriptor descriptor)
        {
            if (UpdateSortDescriptor(CatalogSortDescriptor.Normalize(descriptor.ColumnKey, descriptor.Descending), raiseEvent: false))
            {
                SortDescriptorChanged?.Invoke(this, _sortDescriptor);
            }
        }

        private bool UpdateSortDescriptor(CatalogSortDescriptor descriptor, bool raiseEvent)
        {
            if (_sortDescriptor == descriptor)
            {
                return false;
            }

            _sortDescriptor = descriptor;
            ApplyFilters();
            if (raiseEvent)
            {
                SortDescriptorChanged?.Invoke(this, _sortDescriptor);
            }

            return true;
        }

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
            RefreshPartialCoverageVirtualization();
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
            RefreshPartialCoverageVirtualization();
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

        private IEnumerable<ConfigCatalogItemViewModel> ApplySort(IEnumerable<ConfigCatalogItemViewModel> source)
        {
            var ordered = _sortDescriptor.ColumnKey switch
            {
                CatalogSortColumns.Drift => Sort(source, entry => entry.DriftCount),
                CatalogSortColumns.Coverage => Sort(source, entry => entry.CoverageRatio),
                CatalogSortColumns.Format => Sort(source, entry => entry.Format, StringComparer.OrdinalIgnoreCase),
                CatalogSortColumns.Severity => Sort(source, entry => entry.SeverityRank),
                CatalogSortColumns.Updated => Sort(source, entry => entry.LastUpdated),
                _ => Sort(source, entry => entry.DisplayName, StringComparer.OrdinalIgnoreCase),
            };

            return ordered.ThenBy(entry => entry.DisplayName, StringComparer.OrdinalIgnoreCase);
        }

        private IOrderedEnumerable<ConfigCatalogItemViewModel> Sort<TKey>(IEnumerable<ConfigCatalogItemViewModel> source, Func<ConfigCatalogItemViewModel, TKey> keySelector)
        {
            return Sort(source, keySelector, comparer: null);
        }

        private IOrderedEnumerable<ConfigCatalogItemViewModel> Sort<TKey>(IEnumerable<ConfigCatalogItemViewModel> source, Func<ConfigCatalogItemViewModel, TKey> keySelector, IComparer<TKey>? comparer)
        {
            var comparison = comparer ?? Comparer<TKey>.Default;
            return _sortDescriptor.Descending
                ? source.OrderByDescending(keySelector, comparison)
                : source.OrderBy(keySelector, comparison);
        }

        private void ApplyFilters()
        {
            var filtered = ApplySort(Entries.Where(MatchesFilters)).ToList();

            FilteredEntries.Clear();
            foreach (var entry in filtered)
            {
                FilteredEntries.Add(entry);
            }

            OnPropertyChanged(nameof(HasPartialCoverage));
            OnPropertyChanged(nameof(PartialCoverageEntries));
            ReScanAllPartialCommand.NotifyCanExecuteChanged();
            RefreshPartialCoverageVirtualization();
        }

        private void RefreshPartialCoverageVirtualization()
        {
            var partialCount = Entries.Count(entry => entry.IsPartialCoverage || entry.IsMissingCoverage);
            UseVirtualizedPartialCoverage = _performanceProfile.ShouldVirtualize(partialCount);
        }
    }
}
