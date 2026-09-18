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
using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Models;
using DriftBuster.Backend.Settings;

namespace DriftBuster.Gui.ViewModels
{
    /// <summary>
    /// The settings comparison: one plain line per server, then one table per file (a row per setting, a column per server),
    /// showing only what differs by default.
    /// </summary>
    public sealed partial class CompareViewModel : ObservableObject
    {
        private readonly List<CompareFileViewModel> _files = new();
        private SettingsComparison? _comparison;

        public CompareViewModel()
        {
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
        }

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

        partial void OnStatusMessageChanged(string value) => OnPropertyChanged(nameof(HasStatus));

        public void Load(SettingsComparison? comparison)
        {
            _comparison = comparison;
            _files.Clear();
            Servers.Clear();
            Columns.Clear();
            StatusMessage = string.Empty;
            if (comparison is not null)
            {
                var labels = SettingsComparisonReport.Labels(comparison);
                foreach (var host in comparison.Hosts)
                {
                    Servers.Add(new CompareServerViewModel(host));
                    // Mark the baseline column unless its name already says so.
                    Columns.Add(host.IsBaseline && !host.Label.Contains("baseline", StringComparison.OrdinalIgnoreCase) ? $"{host.Label} (baseline)" : host.Label);
                }

                _files.AddRange(comparison.Files.Select(file => new CompareFileViewModel(file, labels)));
            }

            Headline = BuildHeadline(comparison, ItemNoun);
            OnPropertyChanged(nameof(HasData));
            OnPropertyChanged(nameof(ColumnCount));
            SaveReportCommand.NotifyCanExecuteChanged();
            if (FocusHostId is not null && Servers.All(server => !string.Equals(server.HostId, FocusHostId, StringComparison.Ordinal)))
            {
                FocusHostId = null;
            }
            else
            {
                ApplyFilters();
            }
        }

        public void Reset() => Load(null);

        private void ApplyFilters()
        {
            var search = SearchText.Trim();
            VisibleFiles.Clear();
            foreach (var file in _files)
            {
                if (file.ApplyFilter(DifferencesOnly, FocusHostId, search))
                {
                    VisibleFiles.Add(file);
                }
            }

            EmptyMessage = _files.Count == 0 ? "Run a scan to compare servers."
                : VisibleFiles.Count > 0 ? string.Empty
                : search.Length > 0 ? $"Nothing matches \"{search}\"."
                : DifferencesOnly ? "No differences: every server matches the baseline."
                : "No files.";
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
