using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Models;
using DriftBuster.Gui.Services;

namespace DriftBuster.Gui.ViewModels
{
    /// <summary>
    /// Hunt explorer: scan a folder for dynamic values, then read the findings as a list beside the selected finding, filtered by
    /// rule (the summary chips), by file, or by text.
    /// </summary>
    public partial class HuntViewModel : ObservableObject, IDisposable
    {
        private readonly IDriftbusterService _service;
        private bool _disposed;

        public ObservableCollection<HuntHitView> Hits { get; } = new();

        /// <summary>The findings that pass the rule, file and text filters.</summary>
        public ObservableCollection<HuntHitView> VisibleHits { get; } = new();

        /// <summary>One chip per rule, most findings first.</summary>
        public ObservableCollection<HuntRuleChip> RuleChips { get; } = new();

        [ObservableProperty]
        private HuntHitView? _selectedHit;

        /// <summary>Show only this rule's findings; null for every rule.</summary>
        [ObservableProperty]
        private string? _ruleFilter;

        /// <summary>Show only this file's findings; null for every file.</summary>
        [ObservableProperty]
        private string? _fileFilter;

        [ObservableProperty]
        private string _searchText = string.Empty;

        /// <summary>The results tab: 0 findings, 1 JSON.</summary>
        [ObservableProperty]
        private int _resultTab;

        public IRelayCommand<HuntRuleChip> ToggleRuleCommand { get; }

        public IRelayCommand ClearFiltersCommand { get; }

        public bool HasSelectedHit => SelectedHit is not null;

        public bool HasFilters => RuleFilter is not null || FileFilter is not null || SearchText.Trim().Length > 0;

        public string FilterText
        {
            get
            {
                var parts = new List<string>();
                if (RuleFilter is not null)
                {
                    parts.Add($"rule {RuleFilter}");
                }

                if (FileFilter is not null)
                {
                    parts.Add($"file {FileFilter}");
                }

                if (SearchText.Trim().Length > 0)
                {
                    parts.Add($"\"{SearchText.Trim()}\"");
                }

                return parts.Count == 0
                    ? string.Empty
                    : string.Create(CultureInfo.InvariantCulture, $"Showing {VisibleHits.Count} of {Hits.Count}: {string.Join(", ", parts)}");
            }
        }

        partial void OnSelectedHitChanged(HuntHitView? value) => OnPropertyChanged(nameof(HasSelectedHit));

        partial void OnRuleFilterChanged(string? value)
        {
            foreach (var chip in RuleChips)
            {
                chip.IsSelected = string.Equals(chip.Name, value, StringComparison.Ordinal);
            }

            ApplyFilters();
        }

        partial void OnFileFilterChanged(string? value) => ApplyFilters();

        partial void OnSearchTextChanged(string value) => ApplyFilters();

        private void ApplyFilters()
        {
            var search = SearchText.Trim();
            var selected = SelectedHit;
            VisibleHits.Clear();
            foreach (var hit in Hits.Where(hit =>
                (RuleFilter is null || string.Equals(hit.RuleName, RuleFilter, StringComparison.Ordinal))
                && (FileFilter is null || string.Equals(hit.RelativePath, FileFilter, StringComparison.OrdinalIgnoreCase))
                && (search.Length == 0 || hit.Matches(search))))
            {
                VisibleHits.Add(hit);
            }

            SelectedHit = selected is not null && VisibleHits.Contains(selected) ? selected : VisibleHits.FirstOrDefault();
            OnPropertyChanged(nameof(HasFilters));
            OnPropertyChanged(nameof(FilterText));
        }

        private void RebuildRuleChips()
        {
            RuleChips.Clear();
            foreach (var group in Hits.GroupBy(hit => hit.RuleName, StringComparer.Ordinal).OrderByDescending(group => group.Count()).ThenBy(group => group.Key, StringComparer.Ordinal))
            {
                RuleChips.Add(new HuntRuleChip(group.Key, group.Count()) { IsSelected = string.Equals(group.Key, RuleFilter, StringComparison.Ordinal) });
            }
        }

        [ObservableProperty]
        private string? _directoryPath;

        [ObservableProperty]
        private string? _pattern;

        [ObservableProperty]
        private string? _directoryError;

        [ObservableProperty]
        private string? _errorMessage;

        [ObservableProperty]
        private string? _statusMessage;

        [ObservableProperty]
        private string _rawJson = string.Empty;

        [ObservableProperty]
        private int _resultCount;

        public IAsyncRelayCommand RunHuntCommand { get; }

        public bool IsBusy => RunHuntCommand.IsRunning;

        public bool HasHits => Hits.Count > 0;

        public bool HasDirectoryError => !string.IsNullOrEmpty(DirectoryError);

        public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

        public bool HasStatus => !string.IsNullOrEmpty(StatusMessage);

        public bool HasRawJson => !string.IsNullOrEmpty(RawJson);

        public bool HasNoHits => !HasHits;

        public HuntViewModel(IDriftbusterService service, string? initial = null)
        {
            _service = service;
            StatusMessage = initial;

            Hits.CollectionChanged += OnHitsChanged;

            RunHuntCommand = new AsyncRelayCommand(RunHuntAsync, CanRunHunt);
            ToggleRuleCommand = new RelayCommand<HuntRuleChip>(chip =>
            {
                if (chip is not null)
                {
                    RuleFilter = string.Equals(RuleFilter, chip.Name, StringComparison.Ordinal) ? null : chip.Name;
                }
            });
            ClearFiltersCommand = new RelayCommand(() =>
            {
                RuleFilter = null;
                FileFilter = null;
                SearchText = string.Empty;
            });
            RunHuntCommand.PropertyChanged += (_, args) =>
            {
                if (string.Equals(args.PropertyName, nameof(AsyncRelayCommand.IsRunning), StringComparison.Ordinal))
                {
                    OnPropertyChanged(nameof(IsBusy));
                    RunHuntCommand.NotifyCanExecuteChanged();
                }
            };

            UpdateValidation();
        }

        partial void OnDirectoryPathChanged(string? value) => UpdateValidation();

        private async Task RunHuntAsync()
        {
            ErrorMessage = null;
            StatusMessage = null;

            try
            {
                var result = await _service.HuntAsync(DirectoryPath, Pattern).ConfigureAwait(true);
                ApplyResult(result);
            }
            catch (System.Exception ex)
            {
                ErrorMessage = ErrorText.Plain(ex);
                StatusMessage = null;
                Hits.Clear();
                RuleChips.Clear();
                ApplyFilters();
                ResultCount = 0;
                RawJson = string.Empty;
            }
        }

        private void ApplyResult(HuntResult result)
        {
            Hits.Clear();
            foreach (var hit in result.Hits)
            {
                Hits.Add(new HuntHitView(
                    hit.Rule.Name,
                    hit.Rule.Description,
                    hit.Rule.TokenName,
                    hit.RelativePath,
                    hit.Path,
                    hit.LineNumber,
                    hit.Excerpt));
            }

            if (RuleFilter is not null && Hits.All(hit => !string.Equals(hit.RuleName, RuleFilter, StringComparison.Ordinal)))
            {
                RuleFilter = null;
            }

            if (FileFilter is not null && Hits.All(hit => !string.Equals(hit.RelativePath, FileFilter, StringComparison.OrdinalIgnoreCase)))
            {
                FileFilter = null;
            }

            RebuildRuleChips();
            ApplyFilters();
            ResultTab = 0;

            ResultCount = result.Count;
            RawJson = result.RawJson;
            StatusMessage = result.Count == 0
                ? "No matches found"
                : $"Found {result.Count} hit{(result.Count == 1 ? string.Empty : "s")}.";
            ErrorMessage = null;
        }

        private bool CanRunHunt()
        {
            return string.IsNullOrEmpty(DirectoryError) && !RunHuntCommand.IsRunning;
        }

        private void UpdateValidation()
        {
            DirectoryError = ValidateDirectoryPath(DirectoryPath);
            RunHuntCommand.NotifyCanExecuteChanged();
        }

        private void OnHitsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            OnPropertyChanged(nameof(HasHits));
            OnPropertyChanged(nameof(HasNoHits));
        }

        partial void OnDirectoryErrorChanged(string? value)
        {
            OnPropertyChanged(nameof(HasDirectoryError));
        }

        partial void OnErrorMessageChanged(string? value)
        {
            OnPropertyChanged(nameof(HasError));
        }

        partial void OnStatusMessageChanged(string? value)
        {
            OnPropertyChanged(nameof(HasStatus));
        }

        partial void OnRawJsonChanged(string value)
        {
            OnPropertyChanged(nameof(HasRawJson));
        }

        private static string? ValidateDirectoryPath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return "Select a directory or file";
            }

            if (Directory.Exists(path) || File.Exists(path))
            {
                return null;
            }

            return "Path not found";
        }

        public sealed class HuntHitView
        {
            public HuntHitView(string ruleName, string description, string? tokenName, string relativePath, string fullPath, int lineNumber, string excerpt)
            {
                RuleName = ruleName;
                Description = description;
                TokenName = string.IsNullOrWhiteSpace(tokenName) ? "—" : tokenName;
                RelativePath = relativePath;
                FullPath = fullPath;
                LineNumber = lineNumber;
                Excerpt = TrimExcerpt(excerpt);
                FullExcerpt = excerpt ?? string.Empty;
            }

            /// <summary>The excerpt as captured, for the detail pane.</summary>
            public string FullExcerpt { get; }

            /// <summary>"path:line", the way editors and search tools cite a place in a file.</summary>
            public string Location => string.Create(CultureInfo.InvariantCulture, $"{RelativePath}:{LineNumber}");

            /// <summary>The finding as a JSON object: rule, token, file, line and the full excerpt.</summary>
            public string ToJson() => System.Text.Json.JsonSerializer.Serialize(
                new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["rule"] = RuleName,
                    ["token"] = HasToken ? TokenName : string.Empty,
                    ["file"] = RelativePath,
                    ["line"] = LineNumber,
                    ["excerpt"] = FullExcerpt,
                },
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

            /// <summary>The finding as a tab-separated header and row.</summary>
            public string ToTsv()
            {
                static string Cell(string text) => text.Replace('\t', ' ').Replace("\r", " ", StringComparison.Ordinal).Replace('\n', ' ');
                return "Rule\tToken\tFile\tLine\tExcerpt" + Environment.NewLine
                    + string.Join('\t', Cell(RuleName), Cell(HasToken ? TokenName : string.Empty), Cell(RelativePath), LineNumber.ToString(CultureInfo.InvariantCulture), Cell(FullExcerpt));
            }

            /// <summary>A bug report saying the finding is not a real dynamic value.</summary>
            public BugReportDraft BugReport() => new(
                RelativePath,
                RuleName,
                string.Empty,
                "hunt",
                new Dictionary<string, string>(StringComparer.Ordinal) { ["excerpt"] = FullExcerpt },
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["rule"] = RuleName,
                    ["token"] = HasToken ? TokenName : string.Empty,
                    ["line"] = LineNumber.ToString(CultureInfo.InvariantCulture),
                    ["description"] = Description,
                },
                "Hunt finding is not a real secret or value (false positive)");

            public bool Matches(string search) =>
                RuleName.Contains(search, StringComparison.OrdinalIgnoreCase)
                || RelativePath.Contains(search, StringComparison.OrdinalIgnoreCase)
                || FullExcerpt.Contains(search, StringComparison.OrdinalIgnoreCase)
                || TokenName.Contains(search, StringComparison.OrdinalIgnoreCase);

            public string RuleName { get; }

            public string Description { get; }

            public string TokenName { get; }

            public bool HasToken => !string.Equals(TokenName, "—", StringComparison.Ordinal);

            public string RelativePath { get; }

            public string FullPath { get; }

            public int LineNumber { get; }

            public string Excerpt { get; }

            private static string TrimExcerpt(string text)
            {
                const int limit = 160;
                if (string.IsNullOrEmpty(text) || text.Length <= limit)
                {
                    return text;
                }

                return text[..limit] + "…";
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            Hits.CollectionChanged -= OnHitsChanged;
            _disposed = true;
        }
    }
}
