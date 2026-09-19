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
        private CompareFilter _filter = new();
        private bool _fileMatchesSearch = true;
        private IReadOnlyList<CompareRowViewModel>? _visibleRows;

        public CompareFileViewModel(FileComparison file, IReadOnlyDictionary<string, string> labels)
        {
            _file = file ?? throw new ArgumentNullException(nameof(file));
            ArgumentNullException.ThrowIfNull(labels);
            _rows = file.Settings.Select(row => new CompareRowViewModel(file.Path, row, labels)).ToArray();
            Summary = SettingsComparisonReport.FileSummaryText(file, labels);
            var slash = file.Path.LastIndexOf('/');
            FileName = file.FileLabel.Length > 0 ? file.FileLabel : slash >= 0 ? file.Path[(slash + 1)..] : file.Path;
            Folder = slash >= 0 ? file.Path[..slash] : string.Empty;
            Badge = file.SettingsDiffering > 0
                ? string.Create(CultureInfo.InvariantCulture, $"{file.SettingsDiffering}")
                : file.Presence.Any(value => value.DiffersFromBaseline && value.State == SettingValueState.FileMissing) ? "missing"
                : file.Presence.Any(value => value.DiffersFromBaseline && value.State == SettingValueState.Unreadable) ? "unreadable"
                : file.Presence.Any(value => value.DiffersFromBaseline) ? "extra"
                : string.Empty;
        }

        public string ConfigId => _file.ConfigId;

        /// <summary>The detected format (json, xml, ini, ...).</summary>
        public string Format => _file.Format;

        /// <summary>How the settings were read: settings, lines or binary.</summary>
        public string Mode => _file.Mode;

        /// <summary>The application a rule names for the file; empty when none does.</summary>
        public string AppName => _file.AppName;

        public bool HasAppName => AppName.Length > 0;

        public string Description => _file.Description;

        public bool HasDescription => Description.Length > 0;

        /// <summary>A curation choice or rule leaves the whole file out.</summary>
        public bool Ignored => _file.Ignored;

        /// <summary>Differs before curation: a setting or the file's presence.</summary>
        public bool RawDiffers => _file.Presence.Any(value => value.DiffersFromBaseline) || _rows.Any(row => row.RawDiffers);

        /// <summary>The file's rows under no filter, for actions that span the file.</summary>
        public IReadOnlyList<CompareRowViewModel> AllRows => _rows;

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
        public bool ApplyFilter(bool differencesOnly, string? focusHostId, string search) =>
            ApplyFilter(new CompareFilter { DifferencesOnly = differencesOnly, FocusHostId = focusHostId, Search = search });

        /// <summary>Applies the view's filters; false when the file has nothing to show under them.</summary>
        public bool ApplyFilter(CompareFilter filter)
        {
            _filter = filter ?? throw new ArgumentNullException(nameof(filter));
            _fileMatchesSearch = filter.Search.Length == 0 || Path.Contains(filter.Search, StringComparison.OrdinalIgnoreCase);
            RefreshRows();

            if (Ignored && !filter.ShowIgnored)
            {
                return false;
            }

            var anyRow = _rows.Any(RowMatches);
            if (filter.Narrows)
            {
                return anyRow;
            }

            // With ignored things shown, a file counts as differing if it did before curation.
            var differs = Differs || (filter.ShowIgnored && RawDiffers);
            if (filter.DifferencesOnly && !differs)
            {
                return false;
            }

            var presenceDiffersOnFocus = filter.FocusHostId is not null
                && _file.Presence.Any(value => value.DiffersFromBaseline && string.Equals(value.HostId, filter.FocusHostId, StringComparison.Ordinal));
            if (filter.FocusHostId is not null && !anyRow && !presenceDiffersOnFocus)
            {
                return false;
            }

            return anyRow || (_fileMatchesSearch && (differs || !filter.DifferencesOnly));
        }

        /// <summary>Drops the built row list, so marks and filters are re-read the next time the rows are shown.</summary>
        public void RefreshRows()
        {
            _visibleRows = null;
            OnPropertyChanged(nameof(VisibleRows));
            OnPropertyChanged(nameof(HasVisibleRows));
            OnPropertyChanged(nameof(RowCountText));
        }

        private bool RowMatches(CompareRowViewModel row)
        {
            var filter = _filter;
            if (row.Ignored && !filter.ShowIgnored)
            {
                return false;
            }

            var differs = row.Differs || (filter.ShowIgnored && row.RawDiffers);
            return (!filter.DifferencesOnly || differs)
                && (!filter.ReviewOnly || row.InReview)
                && (filter.Group is null || row.Groups.Contains(filter.Group, StringComparer.OrdinalIgnoreCase))
                && filter.Marks switch
                {
                    CompareMarkFilter.Marked => row.IsMarked,
                    CompareMarkFilter.Unmarked => !row.IsMarked,
                    _ => true,
                }
                && (filter.FocusHostId is null || row.DiffersOn(filter.FocusHostId))
                && (_fileMatchesSearch || row.Matches(filter.Search));
        }

        private static string Count(int count, string noun) => string.Create(CultureInfo.InvariantCulture, $"{count} {noun}{(count == 1 ? string.Empty : "s")}");
    }
}
