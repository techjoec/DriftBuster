using System;
using System.Collections.Generic;
using System.Linq;

using CommunityToolkit.Mvvm.ComponentModel;

using DriftBuster.Backend.Models;

namespace DriftBuster.Gui.ViewModels
{
    /// <summary>One setting across every server.</summary>
    public sealed partial class CompareRowViewModel : ObservableObject
    {
        public CompareRowViewModel(string path, SettingRow row, IReadOnlyDictionary<string, string> labels, bool fileIgnored = false)
        {
            ArgumentNullException.ThrowIfNull(row);
            ArgumentNullException.ThrowIfNull(labels);
            Path = path ?? throw new ArgumentNullException(nameof(path));
            Key = row.Key;
            Differs = row.Differs;
            RawDiffers = row.Values.Any(value => value.DiffersFromBaseline);
            // A setting in an ignored file is shown as ignored too.
            Ignored = row.Ignored || fileIgnored;
            InReview = row.InReview;
            Groups = row.Groups;
            Cells = row.Values.Select(value => new CompareCellViewModel(value, labels.TryGetValue(value.HostId, out var label) ? label : value.HostId)).ToArray();
        }

        /// <summary>The file the setting is in.</summary>
        public string Path { get; }

        public string Key { get; }

        /// <summary>Differs after curation (ignored values and settings left out).</summary>
        public bool Differs { get; }

        /// <summary>Differs before curation.</summary>
        public bool RawDiffers { get; }

        public bool Ignored { get; }

        public bool InReview { get; }

        public IReadOnlyList<string> Groups { get; }

        public IReadOnlyList<CompareCellViewModel> Cells { get; }

        /// <summary>The user's temporary marker.</summary>
        [ObservableProperty]
        private bool _isMarked;

        public bool DiffersOn(string hostId) => Cells.Any(cell => cell.IsDifferent && !cell.IsIgnored && string.Equals(cell.HostId, hostId, StringComparison.Ordinal));

        public bool Matches(string search) =>
            Key.Contains(search, StringComparison.OrdinalIgnoreCase) || Cells.Any(cell => cell.Text.Contains(search, StringComparison.OrdinalIgnoreCase));
    }
}
