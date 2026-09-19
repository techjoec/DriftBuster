using System;

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
            IsIgnored = value.Ignored;
            HostId = value.HostId;
            HostLabel = hostLabel;
            Value = value.State == SettingValueState.Value && !value.Masked ? value.Value : null;
            ValueHash = value.ValueHash;
            CanUnmask = value.Masked && value.SecretValue is not null;
            AutomationName = $"{hostLabel}: {Text}";
        }

        public string HostId { get; }

        public string HostLabel { get; }

        public string Text { get; }

        /// <summary>The value itself when there is one and it is not masked; null otherwise.</summary>
        public string? Value { get; }

        /// <summary>The value's fingerprint, for value-level choices and history; null when there is no value.</summary>
        public string? ValueHash { get; }

        /// <summary>The value differs from the baseline server's.</summary>
        public bool IsDifferent { get; }

        /// <summary>No value: not set, file missing, unreadable or not scanned.</summary>
        public bool IsAbsent { get; }

        public bool IsMasked { get; }

        /// <summary>The value is masked and its text is at hand, so it can be unmasked.</summary>
        public bool CanUnmask { get; }

        /// <summary>A curation choice leaves this value out of the differences.</summary>
        public bool IsIgnored { get; }

        public string AutomationName { get; }
    }
}
