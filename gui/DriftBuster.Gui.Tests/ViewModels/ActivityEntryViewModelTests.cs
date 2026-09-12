using DriftBuster.Gui.ViewModels;

namespace DriftBuster.Gui.Tests.ViewModels;

public sealed class ActivityEntryViewModelTests
{
    private static readonly DateTimeOffset FixedTimestamp = new(2025, 10, 21, 5, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(ActivitySeverity.Error, true, false)]
    [InlineData(ActivitySeverity.Warning, false, true)]
    [InlineData(ActivitySeverity.Info, false, false)]
    [InlineData(ActivitySeverity.Success, false, false)]
    public void Severity_flags_reflect_enum_value(ActivitySeverity severity, bool isError, bool isWarning)
    {
        var entry = new ActivityEntryViewModel(severity, "msg", "", FixedTimestamp, ActivityCategory.General);
        entry.IsError.Should().Be(isError);
        entry.IsWarning.Should().Be(isWarning);
    }

    [Theory]
    [InlineData(ActivityCategory.General, false)]
    [InlineData(ActivityCategory.Export, true)]
    public void IsExport_reflects_category(ActivityCategory category, bool expected)
    {
        var entry = new ActivityEntryViewModel(ActivitySeverity.Info, "msg", "", FixedTimestamp, category);
        entry.IsExport.Should().Be(expected);
    }

    [Fact]
    public void ClipboardText_includes_detail_only_when_present()
    {
        var withDetail = new ActivityEntryViewModel(ActivitySeverity.Error, "Scan failed", "Permission denied", FixedTimestamp, ActivityCategory.General);
        withDetail.ClipboardText.Should().Contain("Scan failed");
        withDetail.ClipboardText.Should().Contain("Permission denied");
        withDetail.TimestampText.Should().Contain("2025");

        var withoutDetail = new ActivityEntryViewModel(ActivitySeverity.Info, "Completed", "", FixedTimestamp, ActivityCategory.General);
        withoutDetail.ClipboardText.Should().Contain("Completed");
        withoutDetail.ClipboardText.Should().NotContain(Environment.NewLine);
    }
}
