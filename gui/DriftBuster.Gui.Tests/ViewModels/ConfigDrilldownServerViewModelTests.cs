using DriftBuster.Backend.Models;
using DriftBuster.Gui.ViewModels;

namespace DriftBuster.Gui.Tests.ViewModels;

public sealed class ConfigDrilldownServerViewModelTests
{
    [Fact]
    public void Constructor_throws_when_detail_is_null()
    {
        var action = () => new ConfigDrilldownServerViewModel(null!);
        action.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Selection_defaults_to_present_and_min_last_seen_reads_not_scanned()
    {
        var present = new ConfigDrilldownServerViewModel(new ConfigServerDetail
        {
            HostId = "host-1",
            Label = "Host 1",
            Present = true,
            Status = "Baseline",
            LastSeen = DateTimeOffset.UtcNow,
        });

        var missing = new ConfigDrilldownServerViewModel(new ConfigServerDetail
        {
            HostId = "host-2",
            Label = "Host 2",
            Present = false,
            Status = "Missing",
            LastSeen = DateTimeOffset.MinValue,
        });

        present.IsSelected.Should().BeTrue();
        present.LastSeenText.Should().NotBe("Not scanned");
        missing.IsSelected.Should().BeFalse();
        missing.LastSeenText.Should().Be("Not scanned");
    }
}
