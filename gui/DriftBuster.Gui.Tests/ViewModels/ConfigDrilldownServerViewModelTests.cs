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
    public void Exposes_detail_properties_and_defaults_selection_to_present()
    {
        var now = DateTimeOffset.UtcNow;
        var detail = new ConfigServerDetail
        {
            HostId = "host-1",
            Label = "Host 1",
            Present = true,
            IsBaseline = true,
            Status = "Baseline",
            DriftLineCount = 3,
            HasSecrets = true,
            Masked = false,
            RedactionStatus = "Visible",
            LastSeen = now,
        };

        var viewModel = new ConfigDrilldownServerViewModel(detail);

        viewModel.Detail.Should().BeSameAs(detail);
        viewModel.IsSelected.Should().BeTrue();
        viewModel.HostId.Should().Be("host-1");
        viewModel.Label.Should().Be("Host 1");
        viewModel.Present.Should().BeTrue();
        viewModel.IsBaseline.Should().BeTrue();
        viewModel.Status.Should().Be("Baseline");
        viewModel.DriftLineCount.Should().Be(3);
        viewModel.HasSecrets.Should().BeTrue();
        viewModel.Masked.Should().BeFalse();
        viewModel.RedactionStatus.Should().Be("Visible");
        viewModel.LastSeen.Should().Be(now);
        viewModel.LastSeenText.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void LastSeenText_returns_not_scanned_for_min_value()
    {
        var detail = new ConfigServerDetail
        {
            HostId = "host-2",
            Label = "Host 2",
            Present = false,
            Status = "Missing",
            LastSeen = DateTimeOffset.MinValue,
        };

        var viewModel = new ConfigDrilldownServerViewModel(detail);

        viewModel.IsSelected.Should().BeFalse();
        viewModel.LastSeenText.Should().Be("Not scanned");
    }
}
