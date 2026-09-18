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
    /// <summary>One setting across every server.</summary>
    public sealed class CompareRowViewModel
    {
        public CompareRowViewModel(SettingRow row, IReadOnlyDictionary<string, string> labels)
        {
            ArgumentNullException.ThrowIfNull(row);
            ArgumentNullException.ThrowIfNull(labels);
            Key = row.Key;
            Differs = row.Differs;
            Cells = row.Values.Select(value => new CompareCellViewModel(value, labels.TryGetValue(value.HostId, out var label) ? label : value.HostId)).ToArray();
        }

        public string Key { get; }

        public bool Differs { get; }

        public IReadOnlyList<CompareCellViewModel> Cells { get; }

        public bool DiffersOn(string hostId) => Cells.Any(cell => cell.IsDifferent && string.Equals(cell.HostId, hostId, StringComparison.Ordinal));

        public bool Matches(string search) =>
            Key.Contains(search, StringComparison.OrdinalIgnoreCase) || Cells.Any(cell => cell.Text.Contains(search, StringComparison.OrdinalIgnoreCase));
    }
}
