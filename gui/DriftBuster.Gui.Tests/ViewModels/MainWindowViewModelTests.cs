using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using Avalonia.Styling;

using DriftBuster.Gui.Services;
using DriftBuster.Gui.Tests.Fakes;
using DriftBuster.Gui.ViewModels;

using FluentAssertions;
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

        var toastService = new ToastService(action => action());
        var runtime = CreateDefaultThemeRuntime();
        var viewModel = new MainWindowViewModel(service, toastService, DiffFactory, HuntFactory, ProfilesFactory, themeRuntime: runtime);

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

        var toastService = new ToastService(action => action());
        var runtime = CreateDefaultThemeRuntime();
        var viewModel = new MainWindowViewModel(service, toastService, _ => new object(), HuntFactory, _ => new object(), themeRuntime: runtime);

        await viewModel.PingCoreCommand.ExecuteAsync(null);

        Assert.Equal("Ping reply: pong", capturedInitial);
        Assert.True(viewModel.IsHuntSelected);

        service.PingAsyncHandler = _ => Task.FromException<string>(new InvalidOperationException("boom"));
        await viewModel.PingCoreCommand.ExecuteAsync(null);
        Assert.Equal("Ping failed: boom", capturedInitial);
        Assert.True(viewModel.IsHuntSelected);
    }

    [Fact]
    public async Task Check_health_sets_status_and_toasts()
    {
        var service = new FakeDriftbusterService { PingResponse = "healthy" };
        var toastService = new ToastService(action => action());
        var runtime = CreateDefaultThemeRuntime();
        var viewModel = new MainWindowViewModel(service, toastService, _ => new object(), (_, _) => new object(), _ => new object(), themeRuntime: runtime);

        await viewModel.CheckHealthCommand.ExecuteAsync(null);

        viewModel.IsBackendHealthy.Should().BeTrue();
        viewModel.BackendStatusText.Should().Contain("Core OK");
        toastService.ActiveToasts.Should().NotBeEmpty();

        toastService.DismissAll();
        service.PingAsyncHandler = _ => Task.FromException<string>(new InvalidOperationException("offline"));
        await viewModel.CheckHealthCommand.ExecuteAsync(null);
        viewModel.IsBackendHealthy.Should().BeFalse();
        viewModel.BackendStatusText.Should().Contain("Core unavailable");
        toastService.ActiveToasts.Should().NotBeEmpty();
    }

    [Fact]
    public void Theme_selector_surfaces_options_and_applies_runtime()
    {
        var options = new List<ThemeOption>
        {
            new("dark-plus", "Dark+", ThemeVariant.Dark, "Palette.DarkPlus"),
            new("light-plus", "Light+", ThemeVariant.Light, "Palette.LightPlus"),
        };

        var runtime = new FakeThemeRuntime(options);
        var service = new FakeDriftbusterService { PingResponse = "pong" };
        var toastService = new ToastService(action => action());

        var viewModel = new MainWindowViewModel(
            service,
            toastService,
            _ => new object(),
            (_, _) => new object(),
            _ => new object(),
            performanceProfile: PerformanceProfile.FromEnvironment(),
            themeRuntime: runtime);

        viewModel.ThemeOptions.Should().Equal(options);
        runtime.Applied.Should().ContainSingle().Which.Id.Should().Be("dark-plus");

        viewModel.SelectedTheme = options[1];
        runtime.Applied.Should().HaveCount(2);
        runtime.Applied[^1].Id.Should().Be("light-plus");
    }

    private static FakeThemeRuntime CreateDefaultThemeRuntime()
    {
        return new FakeThemeRuntime(new List<ThemeOption>
        {
            new("dark-plus", "Dark+", ThemeVariant.Dark, "Palette.DarkPlus"),
        });
    }

    private sealed class FakeThemeRuntime : IThemeRuntime
    {
        private readonly IReadOnlyList<ThemeOption> _options;

        public FakeThemeRuntime(IReadOnlyList<ThemeOption> options)
        {
            _options = options;
        }

        public List<ThemeOption> Applied { get; } = new();

        public IReadOnlyList<ThemeOption> GetAvailableThemes() => _options;

        public ThemeOption GetDefaultTheme(IReadOnlyList<ThemeOption> options)
        {
            return options[0];
        }

        public void ApplyTheme(ThemeOption option)
        {
            Applied.Add(option);
        }
    }
}
