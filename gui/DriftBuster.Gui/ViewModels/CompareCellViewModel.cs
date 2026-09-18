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
    /// <summary>One server's value for one setting, as the Compare table shows it.</summary>
    public sealed class CompareCellViewModel
    {
        public CompareCellViewModel(SettingValue value, string hostLabel)
        {
            ArgumentNullException.ThrowIfNull(value);
            Text = SettingsComparisonReport.CellText(value);
            IsDifferent = value.DiffersFromBaseline;
            IsAbsent = value.State != SettingValueState.Value;
            IsMasked = value.Masked;
            HostId = value.HostId;
            AutomationName = $"{hostLabel}: {Text}";
        }

        public string HostId { get; }

        public string Text { get; }

        /// <summary>The value differs from the baseline server's.</summary>
        public bool IsDifferent { get; }

        /// <summary>No value: not set, file missing, unreadable or not scanned.</summary>
        public bool IsAbsent { get; }

        public bool IsMasked { get; }

        public string AutomationName { get; }
    }
}
