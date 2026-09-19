using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DriftBuster.Backend;
using DriftBuster.Backend.Curation;
using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Models;
using DriftBuster.Backend.Settings;
using DriftBuster.Gui.Services;

namespace DriftBuster.Gui.ViewModels
{
    /// <summary>
    /// The settings comparison: one plain line per server, a list of files, and the selected file's settings table (a row per
    /// setting, a column per server), showing only what differs by default. Previous/next difference walks every differing
    /// setting across the listed files. The user's curation (saved choices and rules, plus this run's choices and marks) is
    /// applied on top of the scan's comparison and re-applied after every change.
    /// </summary>
    public sealed partial class CompareViewModel : ObservableObject, IDisposable
    {
        private readonly List<CompareFileViewModel> _files = new();
        private SettingsComparison? _source;
        private SettingsComparison? _comparison;

        public CompareViewModel(ICurationService? curation = null)
        {
            Curation = curation ?? CurationService.Shared;
            Curation.Changed += OnCurationChanged;
            OpenDetailsCommand = new RelayCommand<CompareFileViewModel>(file =>
            {
                if (file is { HasDetails: true })
                {
                    DetailsRequested?.Invoke(this, new ValueEventArgs<string>(file.ConfigId));
                }
            });
            FocusServerCommand = new RelayCommand<CompareServerViewModel>(server =>
            {
                if (server is null || server.IsBaseline)
                {
                    return;
                }

                FocusHostId = string.Equals(FocusHostId, server.HostId, StringComparison.Ordinal) ? null : server.HostId;
            });
            ClearFocusCommand = new RelayCommand(() => FocusHostId = null);
            SaveReportCommand = new AsyncRelayCommand(SaveReportAsync, () => HasData);
            NextDifferenceCommand = new RelayCommand(() => MoveToDifference(forward: true), () => VisibleFiles.Count > 0);
            PreviousDifferenceCommand = new RelayCommand(() => MoveToDifference(forward: false), () => VisibleFiles.Count > 0);
            ClearGroupFilterCommand = new RelayCommand(() => GroupFilter = null);
            ExportReviewCommand = new AsyncRelayCommand(ExportReviewAsync, () => HasData);
        }

        public ICurationService Curation { get; }

        /// <summary>The host set this comparison belongs to, for choices saved for these servers only.</summary>
        public string HostSetId { get; set; } = CurationScopes.AllRuns;

        public IRelayCommand ClearGroupFilterCommand { get; }

        public IAsyncRelayCommand ExportReviewCommand { get; }

        /// <summary>Show ignored files and settings, dimmed, instead of hiding them.</summary>
        [ObservableProperty]
        private bool _showIgnored;

        [ObservableProperty]
        private CompareMarkFilter _markFilter = CompareMarkFilter.All;

        /// <summary>Show only this group's settings; null for every setting.</summary>
        [ObservableProperty]
        private string? _groupFilter;

        /// <summary>Show only settings on the review list.</summary>
        [ObservableProperty]
        private bool _reviewOnly;

        public IReadOnlyList<CompareMarkFilter> MarkFilters { get; } = Enum.GetValues<CompareMarkFilter>();

        public bool HasGroupFilter => GroupFilter is not null;

        public string GroupFilterText => GroupFilter is null ? string.Empty : $"Showing group \"{GroupFilter}\"";

        /// <summary>How many settings are on the review list, for the review toggle.</summary>
        public int ReviewCount => Curation.Document.Review.Count;

        public string ReviewToggleText => string.Create(CultureInfo.InvariantCulture, $"Review list ({ReviewCount})");

        /// <summary>Why saved curation is not being used, when it could not be read.</summary>
        partial void OnShowIgnoredChanged(bool value) => ApplyFilters();

        partial void OnMarkFilterChanged(CompareMarkFilter value) => ApplyFilters();

        partial void OnReviewOnlyChanged(bool value) => ApplyFilters();

        partial void OnGroupFilterChanged(string? value)
        {
            OnPropertyChanged(nameof(HasGroupFilter));
            OnPropertyChanged(nameof(GroupFilterText));
            ApplyFilters();
        }

        public void Dispose() => Curation.Changed -= OnCurationChanged;

        private void OnCurationChanged(object? sender, EventArgs e) => Refresh();

        /// <summary>Raised after previous/next difference selects a setting, so the view can bring it into view.</summary>
        public event EventHandler? DifferenceSelected;

        /// <summary>Raised with a config id when the user asks for a file's details.</summary>
        public event EventHandler<ValueEventArgs<string>>? DetailsRequested;

        public ObservableCollection<CompareServerViewModel> Servers { get; } = new();

        /// <summary>Server labels in column order, for the table headers.</summary>
        public ObservableCollection<string> Columns { get; } = new();

        public ObservableCollection<CompareFileViewModel> VisibleFiles { get; } = new();

        public IRelayCommand<CompareFileViewModel> OpenDetailsCommand { get; }

        public IRelayCommand<CompareServerViewModel> FocusServerCommand { get; }

        public IRelayCommand ClearFocusCommand { get; }

        public IAsyncRelayCommand SaveReportCommand { get; }

        public IRelayCommand NextDifferenceCommand { get; }

        public IRelayCommand PreviousDifferenceCommand { get; }

        /// <summary>The file list is worth showing only when there is more than one file.</summary>
        public bool ShowFileList => _files.Count > 1;

        [ObservableProperty]
        private CompareFileViewModel? _selectedFile;

        [ObservableProperty]
        private CompareRowViewModel? _selectedRow;

        public bool HasSelectedFile => SelectedFile is not null;

        /// <summary>Where the selected setting sits among the file's differences: "Difference 3 of 6".</summary>
        public string PositionText
        {
            get
            {
                if (SelectedFile is null)
                {
                    return string.Empty;
                }

                var differing = SelectedFile.VisibleRows.Where(row => row.Differs).ToList();
                var index = SelectedRow is null ? -1 : differing.IndexOf(SelectedRow);
                return differing.Count == 0 ? string.Empty
                    : index < 0 ? string.Create(CultureInfo.InvariantCulture, $"{differing.Count} {(differing.Count == 1 ? "difference" : "differences")}")
                    : string.Create(CultureInfo.InvariantCulture, $"Difference {index + 1} of {differing.Count}");
            }
        }

        public bool HasData => _comparison is not null && _files.Count > 0;

        /// <summary>What a column is, for the headline: "server" on Multi-server, "file" in the Diff planner.</summary>
        public string ItemNoun { get; init; } = "server";

        public int ColumnCount => Math.Max(Columns.Count, 1);

        [ObservableProperty]
        private string _headline = string.Empty;

        [ObservableProperty]
        private bool _differencesOnly = true;

        [ObservableProperty]
        private string _searchText = string.Empty;

        [ObservableProperty]
        private string? _focusHostId;

        [ObservableProperty]
        private string _emptyMessage = string.Empty;

        [ObservableProperty]
        private string _statusMessage = string.Empty;

        public bool HasFocus => FocusHostId is not null;

        public string FocusText => FocusHostId is null
            ? string.Empty
            : $"Showing only what differs on {Servers.FirstOrDefault(server => string.Equals(server.HostId, FocusHostId, StringComparison.Ordinal))?.Label ?? FocusHostId}";

        public bool HasEmptyMessage => EmptyMessage.Length > 0;

        public bool HasStatus => StatusMessage.Length > 0;

        partial void OnDifferencesOnlyChanged(bool value) => ApplyFilters();

        partial void OnSearchTextChanged(string value) => ApplyFilters();

        partial void OnFocusHostIdChanged(string? value)
        {
            foreach (var server in Servers)
            {
                server.IsFocused = string.Equals(server.HostId, value, StringComparison.Ordinal);
            }

            OnPropertyChanged(nameof(HasFocus));
            OnPropertyChanged(nameof(FocusText));
            ApplyFilters();
        }

        partial void OnEmptyMessageChanged(string value) => OnPropertyChanged(nameof(HasEmptyMessage));

        partial void OnSelectedFileChanged(CompareFileViewModel? value)
        {
            SelectedRow = null;
            OnPropertyChanged(nameof(HasSelectedFile));
            OnPropertyChanged(nameof(PositionText));
        }

        partial void OnSelectedRowChanged(CompareRowViewModel? value) => OnPropertyChanged(nameof(PositionText));

        partial void OnStatusMessageChanged(string value) => OnPropertyChanged(nameof(HasStatus));

        /// <summary>Shows a scan's comparison with the user's curation applied.</summary>
        public void Load(SettingsComparison? comparison)
        {
            _source = comparison;
            StatusMessage = string.Empty;
            Refresh();
        }

        /// <summary>Re-applies curation to the loaded comparison, keeping the selected file and setting where they still are.</summary>
        public void Refresh()
        {
            var selectedPath = SelectedFile?.Path;
            var selectedKey = SelectedRow?.Key;
            var comparison = _source is null ? null : CurationApplier.Apply(_source, Curation.Document, _sessionChoices, HostSetId);
            _comparison = comparison;
            _files.Clear();
            Servers.Clear();
            // Mark the baseline column unless its name already says so.
            var columns = comparison?.Hosts.Select(host => host.IsBaseline && !host.Label.Contains("baseline", StringComparison.OrdinalIgnoreCase) ? $"{host.Label} (baseline)" : host.Label).ToList() ?? [];
            if (!columns.SequenceEqual(Columns, StringComparer.Ordinal))
            {
                // Only when the servers change: rebuilding the table's columns on every curation change would reset their widths.
                Columns.Clear();
                foreach (var column in columns)
                {
                    Columns.Add(column);
                }
            }

            if (comparison is not null)
            {
                var labels = SettingsComparisonReport.Labels(comparison);
                foreach (var host in comparison.Hosts)
                {
                    Servers.Add(new CompareServerViewModel(host));
                }

                _files.AddRange(comparison.Files.Select(file => new CompareFileViewModel(file, labels)));
                foreach (var row in _files.SelectMany(file => file.AllRows))
                {
                    row.IsMarked = _marks.Contains(MarkKey(row.Path, row.Key));
                }
            }

            OnPropertyChanged(nameof(ReviewCount));
            OnPropertyChanged(nameof(ReviewToggleText));

            Headline = BuildHeadline(comparison, ItemNoun);
            OnPropertyChanged(nameof(HasData));
            OnPropertyChanged(nameof(ColumnCount));
            OnPropertyChanged(nameof(ShowFileList));
            SaveReportCommand.NotifyCanExecuteChanged();
            ExportReviewCommand.NotifyCanExecuteChanged();
            SelectedFile = null;
            if (FocusHostId is not null && Servers.All(server => !string.Equals(server.HostId, FocusHostId, StringComparison.Ordinal)))
            {
                FocusHostId = null;
            }
            else
            {
                ApplyFilters();
            }

            if (selectedPath is not null && VisibleFiles.FirstOrDefault(file => string.Equals(file.Path, selectedPath, StringComparison.OrdinalIgnoreCase)) is { } file)
            {
                SelectedFile = file;
                SelectedRow = selectedKey is null ? null : file.VisibleRows.FirstOrDefault(row => string.Equals(row.Key, selectedKey, StringComparison.Ordinal));
            }
        }

        public void Reset() => Load(null);

        private CompareFilter CurrentFilter() => new()
        {
            DifferencesOnly = DifferencesOnly,
            FocusHostId = FocusHostId,
            Search = SearchText.Trim(),
            ShowIgnored = ShowIgnored,
            Marks = MarkFilter,
            Group = GroupFilter,
            ReviewOnly = ReviewOnly,
        };

        private void ApplyFilters()
        {
            var filter = CurrentFilter();
            var search = filter.Search;
            var selected = SelectedFile;
            VisibleFiles.Clear();
            foreach (var file in _files)
            {
                if (file.ApplyFilter(filter))
                {
                    VisibleFiles.Add(file);
                }
            }

            // Keep the file in view when it still matches; otherwise start at the first one.
            SelectedFile = selected is not null && VisibleFiles.Contains(selected) ? selected : VisibleFiles.FirstOrDefault();
            OnPropertyChanged(nameof(PositionText));
            NextDifferenceCommand.NotifyCanExecuteChanged();
            PreviousDifferenceCommand.NotifyCanExecuteChanged();

            EmptyMessage = _files.Count == 0 ? "Run a scan to compare servers."
                : VisibleFiles.Count > 0 ? string.Empty
                : search.Length > 0 ? $"Nothing matches \"{search}\"."
                : ReviewOnly ? "The review list is empty. Right-click a setting and choose Add to report."
                : GroupFilter is not null ? $"Nothing in group \"{GroupFilter}\" shows under these filters."
                : MarkFilter == CompareMarkFilter.Marked ? "Nothing is marked. Right-click a setting and choose Mark."
                : DifferencesOnly ? "No differences: every server matches the baseline."
                : "No files.";
        }

        /// <summary>
        /// Selects the next (or previous) differing setting, moving on to the next listed file that differs when the selected
        /// file has no more. A file that differs only by being missing or extra is a stop of its own, with no setting selected.
        /// </summary>
        private void MoveToDifference(bool forward)
        {
            if (VisibleFiles.Count == 0)
            {
                return;
            }

            var fileIndex = SelectedFile is null ? -1 : VisibleFiles.IndexOf(SelectedFile);
            if (fileIndex >= 0 && SelectRowInFile(VisibleFiles[fileIndex], SelectedRow, forward))
            {
                return;
            }

            for (var step = 1; step <= VisibleFiles.Count; step++)
            {
                var index = fileIndex < 0
                    ? (forward ? step - 1 : VisibleFiles.Count - step)
                    : ((fileIndex + (forward ? step : -step)) % VisibleFiles.Count + VisibleFiles.Count) % VisibleFiles.Count;
                var file = VisibleFiles[index];
                if (!file.Differs)
                {
                    continue;
                }

                SelectedFile = file;
                SelectRowInFile(file, null, forward);
                return;
            }
        }

        private bool SelectRowInFile(CompareFileViewModel file, CompareRowViewModel? from, bool forward)
        {
            var rows = file.VisibleRows;
            var start = from is null ? (forward ? -1 : rows.Count) : IndexOf(rows, from);
            for (var index = forward ? start + 1 : start - 1; index >= 0 && index < rows.Count; index += forward ? 1 : -1)
            {
                if (rows[index].Differs)
                {
                    SelectedRow = rows[index];
                    DifferenceSelected?.Invoke(this, EventArgs.Empty);
                    return true;
                }
            }

            return false;
        }

        private static int IndexOf(IReadOnlyList<CompareRowViewModel> rows, CompareRowViewModel row)
        {
            for (var index = 0; index < rows.Count; index++)
            {
                if (ReferenceEquals(rows[index], row))
                {
                    return index;
                }
            }

            return -1;
        }

        private static string BuildHeadline(SettingsComparison? comparison, string noun)
        {
            if (comparison is null || comparison.Hosts.Length == 0)
            {
                return string.Empty;
            }

            var others = comparison.Hosts.Where(host => !host.IsBaseline).ToList();
            var differing = others.Count(host => host.Scanned && !host.MatchesBaseline);
            var failed = others.Count(host => !host.Scanned);
            var baseline = comparison.Hosts.FirstOrDefault(host => host.IsBaseline)?.Label ?? comparison.BaselineHostId;
            var text = differing == 0
                ? $"All {noun}s match {baseline}."
                : string.Create(CultureInfo.InvariantCulture, $"{differing} of {others.Count} {(others.Count == 1 ? noun + " differs" : noun + "s differ")} from {baseline}.");
            return failed == 0 ? text : string.Create(CultureInfo.InvariantCulture, $"{text} {failed} could not be scanned.");
        }

        private async Task SaveReportAsync()
        {
            if (_comparison is null)
            {
                return;
            }

            try
            {
                var directory = DriftbusterPaths.GetExportDirectory();
                var stamp = DateTimeOffset.UtcNow;
                var name = $"settings-comparison-{stamp:yyyyMMddHHmmss}";
                var html = Path.Combine(directory, name + ".html");
                var csv = Path.Combine(directory, name + ".csv");
                await File.WriteAllTextAsync(html, SettingsComparisonReport.Html(_comparison, stamp)).ConfigureAwait(true);
                await File.WriteAllTextAsync(csv, SettingsComparisonReport.Csv(_comparison)).ConfigureAwait(true);
                StatusMessage = $"Saved {html} and {Path.GetFileName(csv)}.";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                StatusMessage = $"Could not save the report: {ErrorText.Plain(ex)}";
            }
        }
    }
}
