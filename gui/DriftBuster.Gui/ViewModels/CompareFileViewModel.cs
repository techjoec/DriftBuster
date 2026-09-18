using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using CommunityToolkit.Mvvm.ComponentModel;

using DriftBuster.Backend.Models;
using DriftBuster.Backend.Settings;

namespace DriftBuster.Gui.ViewModels
{
    /// <summary>
    /// One file: its entry in the file list and, once selected, its settings table under the Compare view's filters.
    /// The row list is built only when asked for, so filtering many large files stays cheap.
    /// </summary>
    public sealed partial class CompareFileViewModel : ObservableObject
    {
        private readonly FileComparison _file;
        private readonly IReadOnlyList<CompareRowViewModel> _rows;
        private bool _differencesOnly;
        private string? _focusHostId;
        private string _search = string.Empty;
        private bool _fileMatchesSearch = true;
        private IReadOnlyList<CompareRowViewModel>? _visibleRows;

        public CompareFileViewModel(FileComparison file, IReadOnlyDictionary<string, string> labels)
        {
            _file = file ?? throw new ArgumentNullException(nameof(file));
            ArgumentNullException.ThrowIfNull(labels);
            _rows = file.Settings.Select(row => new CompareRowViewModel(row, labels)).ToArray();
            Summary = SettingsComparisonReport.FileSummaryText(file, labels);
            var slash = file.Path.LastIndexOf('/');
            FileName = slash >= 0 ? file.Path[(slash + 1)..] : file.Path;
            Folder = slash >= 0 ? file.Path[..slash] : string.Empty;
            Badge = file.SettingsDiffering > 0
                ? string.Create(CultureInfo.InvariantCulture, $"{file.SettingsDiffering}")
                : file.Presence.Any(value => value.DiffersFromBaseline && value.State == SettingValueState.FileMissing) ? "missing"
                : file.Presence.Any(value => value.DiffersFromBaseline && value.State == SettingValueState.Unreadable) ? "unreadable"
                : file.Presence.Any(value => value.DiffersFromBaseline) ? "extra"
                : string.Empty;
        }

        public string ConfigId => _file.ConfigId;

        public string Path => _file.Path;

        public string FileName { get; }

        public string Folder { get; }

        public bool HasFolder => Folder.Length > 0;

        public string Summary { get; }

        /// <summary>What the file list shows beside the name: the number of differing settings, or why the file differs.</summary>
        public string Badge { get; }

        public bool HasBadge => Badge.Length > 0;

        public bool Differs => _file.Differs;

        public bool HasDetails => ConfigId.Length > 0;

        public int SettingCount => _rows.Count;

        /// <summary>The settings shown under the current filters, in file order.</summary>
        public IReadOnlyList<CompareRowViewModel> VisibleRows => _visibleRows ??= _rows.Where(RowMatches).ToArray();

        public bool HasVisibleRows => VisibleRows.Count > 0;

        public string RowCountText => VisibleRows.Count == _rows.Count
            ? Count(_rows.Count, "setting")
            : string.Create(CultureInfo.InvariantCulture, $"{VisibleRows.Count} of {Count(_rows.Count, "setting")} shown");

        /// <summary>Applies the view's filters; false when the file has nothing to show under them.</summary>
        public bool ApplyFilter(bool differencesOnly, string? focusHostId, string search)
        {
            _differencesOnly = differencesOnly;
            _focusHostId = focusHostId;
            _search = search;
            _fileMatchesSearch = search.Length == 0 || Path.Contains(search, StringComparison.OrdinalIgnoreCase);
            _visibleRows = null;
            OnPropertyChanged(nameof(VisibleRows));
            OnPropertyChanged(nameof(HasVisibleRows));
            OnPropertyChanged(nameof(RowCountText));

            if (differencesOnly && !Differs)
            {
                return false;
            }

            var anyRow = _rows.Any(RowMatches);
            var presenceDiffersOnFocus = focusHostId is not null
                && _file.Presence.Any(value => value.DiffersFromBaseline && string.Equals(value.HostId, focusHostId, StringComparison.Ordinal));
            if (focusHostId is not null && !anyRow && !presenceDiffersOnFocus)
            {
                return false;
            }

            return anyRow || (_fileMatchesSearch && (Differs || !differencesOnly));
        }

        private bool RowMatches(CompareRowViewModel row) =>
            (!_differencesOnly || row.Differs)
            && (_focusHostId is null || row.DiffersOn(_focusHostId))
            && (_fileMatchesSearch || row.Matches(_search));

        private static string Count(int count, string noun) => string.Create(CultureInfo.InvariantCulture, $"{count} {noun}{(count == 1 ? string.Empty : "s")}");
    }
}
