using System;
using System.Threading.Tasks;

using DriftBuster.Gui.Services;
using DriftBuster.Gui.Tests.Fakes;
using DriftBuster.Gui.ViewModels;

using Xunit;

namespace DriftBuster.Gui.Tests.ViewModels;

public class MainWindowViewModelTests
{
    [Fact]
    public void Commands_swap_views_using_factories()
    {
        var service = new FakeDriftbusterService { PingResponse = "pong" };
        var diffView = new object();
        var profilesView = new object();
        object? huntView = null;
        string? capturedInitial = null;

        object DiffFactory(IDriftbusterService _) => diffView;
        object HuntFactory(IDriftbusterService _, string? initial)
        {
            capturedInitial = initial;
            huntView = new object();
            return huntView!;
        }

        object ProfilesFactory(IDriftbusterService _) => profilesView;

        var viewModel = new MainWindowViewModel(service, DiffFactory, HuntFactory, ProfilesFactory);

        Assert.Same(diffView, viewModel.CurrentView);
        Assert.True(viewModel.IsDiffSelected);
        Assert.False(viewModel.IsHuntSelected);

        viewModel.ShowDiffCommand.Execute(null);
        Assert.Same(diffView, viewModel.CurrentView);
        Assert.True(viewModel.IsDiffSelected);

        viewModel.ShowHuntCommand.Execute(null);
        Assert.Same(huntView, viewModel.CurrentView);
        Assert.False(viewModel.IsDiffSelected);
        Assert.True(viewModel.IsHuntSelected);

        viewModel.ShowProfilesCommand.Execute(null);
        Assert.Same(profilesView, viewModel.CurrentView);
        Assert.True(viewModel.IsProfilesSelected);

        viewModel.ShowHunt("initial message");
        Assert.Equal("initial message", capturedInitial);
    }

    [Fact]
    public async Task Ping_command_routes_status_to_hunt_view()
    {
        var service = new FakeDriftbusterService { PingResponse = "pong" };
        string? capturedInitial = null;
        object HuntFactory(IDriftbusterService _, string? initial)
        {
            capturedInitial = initial;
            return new object();
        }

        var viewModel = new MainWindowViewModel(service, _ => new object(), HuntFactory, _ => new object());

        await viewModel.PingCoreCommand.ExecuteAsync(null);

        Assert.Equal("Ping reply: pong", capturedInitial);
        Assert.True(viewModel.IsHuntSelected);

        service.PingAsyncHandler = _ => Task.FromException<string>(new InvalidOperationException("boom"));
        await viewModel.PingCoreCommand.ExecuteAsync(null);
        Assert.Equal("Ping failed: boom", capturedInitial);
        Assert.True(viewModel.IsHuntSelected);
    }
}
