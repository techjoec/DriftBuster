using DriftBuster.Backend.Models;
using DriftBuster.Gui.Services;
using DriftBuster.Gui.Tests.Fakes;
using DriftBuster.Gui.ViewModels;

namespace DriftBuster.Gui.Tests.ViewModels;

/// <summary>What the Setup host list says about each host, and which host its editor shows.</summary>
public sealed class ServerSlotSummaryTests
{
    private static ServerSelectionViewModel Create() =>
        new(new FakeDriftbusterService(), new ToastService(action => action()), new InMemorySessionCacheService(), curation: new InMemoryCurationService());

    [Fact]
    public void Hosts_read_as_one_line_and_a_word_of_state()
    {
        using var viewModel = Create();
        var host = viewModel.Servers[0];
        var off = viewModel.Servers[5];

        off.SummaryText.Should().Be("Not scanned");
        off.ShortStatus.Should().Be("Off");

        host.Scope = ServerScanScope.CustomRoots;
        host.ReplaceRoots([new RootEntryViewModel("C:\\apps"), new RootEntryViewModel("D:\\more")]);
        host.SummaryText.Should().Be("Custom roots: C:\\apps (+1 more)");
        host.ReplaceRoots([new RootEntryViewModel("C:\\apps")]);
        host.SummaryText.Should().Be("Custom roots: C:\\apps");

        host.HasLastRun.Should().BeFalse();
        host.MarkState(ServerScanStatus.Succeeded, "Evaluated 21 configuration(s).");
        host.ShortStatus.Should().Be("Done");
        host.HasLastRun.Should().BeTrue();
        host.LastRunText.Should().StartWith("Last run: ");
        host.MarkState(ServerScanStatus.Failed, "Access denied");
        host.ShortStatus.Should().Be("Failed");
        host.MarkState(ServerScanStatus.Running, "Scanning");
        host.ShortStatus.Should().Be("Scanning");
    }

    [Fact]
    public void The_first_host_is_selected_and_a_new_host_takes_the_selection()
    {
        using var viewModel = Create();

        viewModel.SelectedServer.Should().BeSameAs(viewModel.Servers[0]);
        viewModel.HasSelectedServer.Should().BeTrue();

        viewModel.AddServerCommand.Execute(null);

        viewModel.SelectedServer.Should().BeSameAs(viewModel.Servers[^1]);
    }
}
