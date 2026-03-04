using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

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

    [Fact]
    public async Task Ping_failure_copy_details_action_executes_without_throwing()
    {
        var service = new FakeDriftbusterService
        {
            PingAsyncHandler = _ => Task.FromException<string>(new InvalidOperationException("copy-path")),
        };
        var toast = new ToastService(action => action());
        var viewModel = new MainWindowViewModel(
            service,
            toast,
            _ => new object(),
            (_, _) => new object(),
            _ => new object(),
            themeRuntime: new FakeThemeRuntime());

        await viewModel.PingCoreCommand.ExecuteAsync(null);

        var failureToast = toast.ActiveToasts.Single(entry => string.Equals(entry.Title, "Ping failed", StringComparison.Ordinal));
        failureToast.PrimaryCommand.Should().NotBeNull();
        var action = () => failureToast.PrimaryCommand!.ExecuteAsync(null);
        await action.Should().NotThrowAsync();
    }

    private sealed class FakeThemeRuntime : IThemeRuntime
    {
        private static readonly IReadOnlyList<ThemeOption> Options = new[]
        {
            new ThemeOption("dark-plus", "Dark+", Avalonia.Styling.ThemeVariant.Dark, "Palette.DarkPlus"),
        };

        public IReadOnlyList<ThemeOption> GetAvailableThemes() => Options;

        public ThemeOption GetDefaultTheme(IReadOnlyList<ThemeOption> options) => options[0];

        public void ApplyTheme(ThemeOption option)
        {
        }
    }
}
