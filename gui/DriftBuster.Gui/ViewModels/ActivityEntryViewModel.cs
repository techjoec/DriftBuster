using System;

using CommunityToolkit.Mvvm.ComponentModel;

namespace DriftBuster.Gui.ViewModels
{
    public enum ActivitySeverity
    {
        Info,
        Success,
        Warning,
        Error,
    }

    public enum ActivityCategory
    {
        General,
        Export,
    }

    public sealed partial class ActivityEntryViewModel : ObservableObject
    {
        public ActivityEntryViewModel(ActivitySeverity severity, string summary, string detail, DateTimeOffset timestamp, ActivityCategory category)
        {
            Severity = severity;
            Summary = summary;
            Detail = detail;
            Timestamp = timestamp;
            Category = category;
            Id = Guid.NewGuid();
        }

        public Guid Id { get; }

        public ActivitySeverity Severity { get; }

        public string Summary { get; }

        public string Detail { get; }

        public DateTimeOffset Timestamp { get; }

        public ActivityCategory Category { get; }

        public string TimestampText => Timestamp.ToLocalTime().ToString("g");

        public string SeverityLabel => Severity.ToString();

        public string ClipboardText => string.IsNullOrWhiteSpace(Detail)
            ? $"[{Timestamp:u}] {Summary}"
            : $"[{Timestamp:u}] {Summary}{Environment.NewLine}{Detail}";

        public bool IsError => Severity == ActivitySeverity.Error;

        public bool IsWarning => Severity == ActivitySeverity.Warning;

        public bool IsExport => Category == ActivityCategory.Export;
    }
}
