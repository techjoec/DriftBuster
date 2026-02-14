using System;
using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;

using DriftBuster.Backend.Models;

namespace DriftBuster.Gui.ViewModels
{
    public sealed partial class ConfigDrilldownServerViewModel : ObservableObject
    {
        public ConfigDrilldownServerViewModel(ConfigServerDetail detail)
        {
            Detail = detail ?? throw new ArgumentNullException(nameof(detail));
            _isSelected = detail.Present;
        }

        public ConfigServerDetail Detail { get; }

        [ObservableProperty]
        private bool _isSelected;

        public string HostId => Detail.HostId;

        public string Label => Detail.Label;

        public bool Present => Detail.Present;

        public bool IsBaseline => Detail.IsBaseline;

        public string Status => Detail.Status;

        public int DriftLineCount => Detail.DriftLineCount;

        public bool HasSecrets => Detail.HasSecrets;

        public bool Masked => Detail.Masked;

        public string RedactionStatus => Detail.RedactionStatus;

        public DateTimeOffset LastSeen => Detail.LastSeen;

        public string LastSeenText => Detail.LastSeen == DateTimeOffset.MinValue
            ? "Not scanned"
            : Detail.LastSeen.ToLocalTime().ToString("g", CultureInfo.InvariantCulture);
    }
}
