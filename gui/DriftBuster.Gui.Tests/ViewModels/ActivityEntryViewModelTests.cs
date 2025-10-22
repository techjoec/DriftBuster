using System;
using DriftBuster.Gui.ViewModels;
using FluentAssertions;
using Xunit;

namespace DriftBuster.Gui.Tests.ViewModels;

public sealed class ActivityEntryViewModelTests
{
    [Fact]
    public void Clipboard_and_labels_reflect_severity_and_detail()
    {
        var timestamp = new DateTimeOffset(2025, 10, 21, 5, 0, 0, TimeSpan.Zero);
        var entry = new ActivityEntryViewModel(ActivitySeverity.Error, "Scan failed", "Permission denied", timestamp, ActivityCategory.General);

        entry.Id.Should().NotBe(Guid.Empty);
        entry.TimestampText.Should().Contain("2025");
        entry.SeverityLabel.Should().Be("Error");
        entry.IsError.Should().BeTrue();
        entry.ClipboardText.Should().Contain("Scan failed");
        entry.ClipboardText.Should().Contain("Permission denied");

        var infoEntry = new ActivityEntryViewModel(ActivitySeverity.Info, "Completed", string.Empty, timestamp, ActivityCategory.General);
        infoEntry.IsError.Should().BeFalse();
        infoEntry.ClipboardText.Should().Contain("Completed");
        infoEntry.ClipboardText.Should().NotContain("Permission denied");
    }
}
