using DriftBuster.Backend.Models;
using DriftBuster.Gui.ViewModels;

namespace DriftBuster.Gui.Tests.ViewModels;

public sealed class ScanScopeOptionTests
{
    [Fact]
    public void Constructor_assigns_value_and_display_name()
    {
        var option = new ScanScopeOption(ServerScanScope.CustomRoots, "Custom roots");

        option.Value.Should().Be(ServerScanScope.CustomRoots);
        option.DisplayName.Should().Be("Custom roots");
    }
}
