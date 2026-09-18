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
    /// <summary>One server's line in the summary strip.</summary>
    public sealed partial class CompareServerViewModel : ObservableObject
    {
        public CompareServerViewModel(HostComparisonSummary host)
        {
            ArgumentNullException.ThrowIfNull(host);
            HostId = host.HostId;
            Label = host.Label;
            Summary = SettingsComparisonReport.HostSummaryText(host);
            IsBaseline = host.IsBaseline;
            Matches = host.MatchesBaseline;
            Failed = !host.Scanned;
            Differs = host.Scanned && !host.IsBaseline && !host.MatchesBaseline;
        }

        public string HostId { get; }

        public string Label { get; }

        public string Summary { get; }

        public bool IsBaseline { get; }

        public bool Matches { get; }

        public bool Differs { get; }

        public bool Failed { get; }

        /// <summary>True when the view shows only what differs on this server.</summary>
        [ObservableProperty]
        private bool _isFocused;
    }
}
