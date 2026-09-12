using Avalonia.Headless.XUnit;
using DriftBuster.Gui.Services;
using DriftBuster.Gui.Tests.Fakes;
using DriftBuster.Gui.Tests.Ui;
using DriftBuster.Gui.ViewModels;
using DriftBuster.Gui.Views;

namespace DriftBuster.Gui.Tests.ViewModels;

[Collection(HeadlessCollection.Name)]
public sealed class MainWindowViewModelFactoryTests
{
    [AvaloniaFact]
    public void Default_factories_create_views_through_public_navigation()
    {
        var service = new FakeDriftbusterService { PingResponse = "pong" };
        var viewModel = new MainWindowViewModel(service, new ToastService(action => action()));

        viewModel.CurrentView.Should().BeOfType<DiffView>();
        ((DiffView)viewModel.CurrentView!).DataContext.Should().BeOfType<DiffViewModel>();

        viewModel.ShowHunt("seed");
        viewModel.CurrentView.Should().BeOfType<HuntView>();
        ((HuntView)viewModel.CurrentView!).DataContext.Should().BeOfType<HuntViewModel>();

        viewModel.ShowProfiles();
        viewModel.CurrentView.Should().BeOfType<RunProfilesView>();
        ((RunProfilesView)viewModel.CurrentView!).DataContext.Should().BeOfType<RunProfilesViewModel>();

        viewModel.ShowMultiServer();
        viewModel.CurrentView.Should().BeOfType<ServerSelectionView>();
        ((ServerSelectionView)viewModel.CurrentView!).DataContext.Should().BeOfType<ServerSelectionViewModel>();
    }
}
