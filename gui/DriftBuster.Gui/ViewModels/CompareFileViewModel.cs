using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;

using CommunityToolkit.Mvvm.ComponentModel;

using DriftBuster.Backend.Models;
using DriftBuster.Backend.Settings;

namespace DriftBuster.Gui.ViewModels
{
    /// <summary>One file: a plain header and its settings table, filtered by the Compare view's controls.</summary>
    public sealed partial class CompareFileViewModel : ObservableObject
    {
        /// <summary>Rows shown per file; a file with more asks the user to search.</summary>
        internal const int MaxVisibleRows = 500;

        private readonly FileComparison _file;
        private readonly IReadOnlyList<CompareRowViewModel> _rows;

        public CompareFileViewModel(FileComparison file, IReadOnlyDictionary<string, string> labels)
        {
            _file = file ?? throw new ArgumentNullException(nameof(file));
            ArgumentNullException.ThrowIfNull(labels);
            _rows = file.Settings.Select(row => new CompareRowViewModel(row, labels)).ToArray();
            Summary = SettingsComparisonReport.FileSummaryText(file, labels);
            var slash = file.Path.LastIndexOf('/');
            FileName = slash >= 0 ? file.Path[(slash + 1)..] : file.Path;
            Folder = slash >= 0 ? file.Path[..slash] : string.Empty;
        }

        public string ConfigId => _file.ConfigId;

        public string Path => _file.Path;

        public string FileName { get; }

        public string Folder { get; }

        public bool HasFolder => Folder.Length > 0;

        public string Summary { get; }

        public bool Differs => _file.Differs;

        public bool HasDetails => ConfigId.Length > 0;

        public ObservableCollection<CompareRowViewModel> VisibleRows { get; } = new();

        public bool HasVisibleRows => VisibleRows.Count > 0;

        [ObservableProperty]
        private string _rowNote = string.Empty;

        public bool HasRowNote => RowNote.Length > 0;

        partial void OnRowNoteChanged(string value) => OnPropertyChanged(nameof(HasRowNote));

        /// <summary>Applies the view's filters; false when the file has nothing to show under them.</summary>
        public bool ApplyFilter(bool differencesOnly, string? focusHostId, string search)
        {
            var fileMatchesSearch = search.Length == 0 || Path.Contains(search, StringComparison.OrdinalIgnoreCase);
            var presenceDiffersOnFocus = focusHostId is not null
                && _file.Presence.Any(value => value.DiffersFromBaseline && string.Equals(value.HostId, focusHostId, StringComparison.Ordinal));
            var rows = _rows.Where(row =>
                (!differencesOnly || row.Differs)
                && (focusHostId is null || row.DiffersOn(focusHostId))
                && (fileMatchesSearch || row.Matches(search))).ToList();

            VisibleRows.Clear();
            foreach (var row in rows.Take(MaxVisibleRows))
            {
                VisibleRows.Add(row);
            }

            OnPropertyChanged(nameof(HasVisibleRows));
            RowNote = rows.Count > MaxVisibleRows
                ? string.Create(CultureInfo.InvariantCulture, $"Showing the first {MaxVisibleRows} of {rows.Count} settings. Search to narrow the list.")
                : string.Empty;

            if (differencesOnly && !Differs)
            {
                return false;
            }

            if (focusHostId is not null && rows.Count == 0 && !presenceDiffersOnFocus)
            {
                return false;
            }

            return rows.Count > 0 || (fileMatchesSearch && (Differs || !differencesOnly));
        }
    }
}
