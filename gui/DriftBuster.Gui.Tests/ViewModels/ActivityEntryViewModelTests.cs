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
        entry.SeverityLabel.Should().Be(severity.ToString());
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
    public void Id_is_unique_per_instance()
    {
        var a = new ActivityEntryViewModel(ActivitySeverity.Info, "a", "", FixedTimestamp, ActivityCategory.General);
        var b = new ActivityEntryViewModel(ActivitySeverity.Info, "b", "", FixedTimestamp, ActivityCategory.General);
        a.Id.Should().NotBe(Guid.Empty);
        a.Id.Should().NotBe(b.Id);
    }

    [Fact]
    public void TimestampText_contains_year()
    {
        var entry = new ActivityEntryViewModel(ActivitySeverity.Info, "msg", "", FixedTimestamp, ActivityCategory.General);
        entry.TimestampText.Should().Contain("2025");
    }

    [Fact]
    public void ClipboardText_includes_detail_when_present()
    {
        var entry = new ActivityEntryViewModel(ActivitySeverity.Error, "Scan failed", "Permission denied", FixedTimestamp, ActivityCategory.General);
        entry.ClipboardText.Should().Contain("Scan failed");
        entry.ClipboardText.Should().Contain("Permission denied");
    }

    [Fact]
    public void ClipboardText_omits_detail_when_empty()
    {
        var entry = new ActivityEntryViewModel(ActivitySeverity.Info, "Completed", "", FixedTimestamp, ActivityCategory.General);
        entry.ClipboardText.Should().Contain("Completed");
        entry.ClipboardText.Should().NotContain(Environment.NewLine);
    }
}
