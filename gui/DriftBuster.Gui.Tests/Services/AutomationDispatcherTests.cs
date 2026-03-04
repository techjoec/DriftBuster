#if DEBUG
using System.Text.Json;

using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;

using DriftBuster.Backend.Models;
using DriftBuster.Gui.Services;
using DriftBuster.Gui.Tests.Fakes;
using DriftBuster.Gui.Tests.Ui;
using DriftBuster.Gui.ViewModels;

namespace DriftBuster.Gui.Tests.Services;

[Collection(HeadlessCollection.Name)]
public sealed class AutomationDispatcherTests
{
    [AvaloniaFact]
    public void Navigate_multi_server_catalog_requires_catalog_entries()
    {
        var fixture = CreateFixture();

        using var response = fixture.Dispatch(new { cmd = "navigate", tab = "multi-server/catalog" });

        response.RootElement.GetProperty("ok").GetBoolean().Should().BeFalse();
        response.RootElement.GetProperty("error").GetString().Should().Contain("Catalog view is unavailable");
    }

    [AvaloniaTheory]
    [InlineData("multi-server/catalog")]
    [InlineData("multiserver/catalog")]
    public void Navigate_multi_server_catalog_activates_catalog_view_when_entries_exist(string tab)
    {
        var fixture = CreateFixture();
        fixture.PopulateCatalog();

        using var response = fixture.Dispatch(new { cmd = "navigate", tab });

        response.RootElement.GetProperty("ok").GetBoolean().Should().BeTrue();
        fixture.ServerViewModel.IsViewingCatalog.Should().BeTrue();
        fixture.ServerViewModel.IsViewingDrilldown.Should().BeFalse();
    }

    [AvaloniaFact]
    public void Navigate_multi_server_drilldown_requires_loaded_drilldown()
    {
        var fixture = CreateFixture();

        using var response = fixture.Dispatch(new { cmd = "navigate", tab = "multi-server/drilldown" });

        response.RootElement.GetProperty("ok").GetBoolean().Should().BeFalse();
        response.RootElement.GetProperty("error").GetString().Should().Contain("Drilldown view is unavailable");
    }

    [AvaloniaFact]
    public void Navigate_multi_server_drilldown_activates_drilldown_when_available()
    {
        var fixture = CreateFixture();
        fixture.PopulateDrilldown();

        using var response = fixture.Dispatch(new { cmd = "navigate", tab = "multi-server/drilldown" });

        response.RootElement.GetProperty("ok").GetBoolean().Should().BeTrue();
        fixture.ServerViewModel.IsViewingCatalog.Should().BeFalse();
        fixture.ServerViewModel.IsViewingDrilldown.Should().BeTrue();
    }

    [AvaloniaTheory]
    [InlineData("multi-server/setup")]
    [InlineData("multiserver/setup")]
    public void Navigate_multi_server_setup_returns_to_setup_sub_view(string tab)
    {
        var fixture = CreateFixture();
        fixture.PopulateCatalog();
        fixture.ServerViewModel.ShowCatalogCommand.Execute(null);

        using var response = fixture.Dispatch(new { cmd = "navigate", tab });

        response.RootElement.GetProperty("ok").GetBoolean().Should().BeTrue();
        fixture.ServerViewModel.IsViewingCatalog.Should().BeFalse();
        fixture.ServerViewModel.IsViewingDrilldown.Should().BeFalse();
    }

    [AvaloniaFact]
    public void Navigate_rejects_unknown_multi_server_sub_view()
    {
        var fixture = CreateFixture();

        using var response = fixture.Dispatch(new { cmd = "navigate", tab = "multi-server/unknown" });

        response.RootElement.GetProperty("ok").GetBoolean().Should().BeFalse();
        response.RootElement.GetProperty("error").GetString().Should().Contain("Unknown multi-server sub-view");
    }

    private static DispatcherFixture CreateFixture()
    {
        var service = new FakeDriftbusterService();
        var toast = new ToastService(action => action());
        var serverViewModel = new ServerSelectionViewModel(service, toast, new InMemorySessionCacheService());

        object ServerSelectionFactory(IDriftbusterService _, IToastService __, PerformanceProfile ___)
            => new Border { DataContext = serverViewModel };

        var mainViewModel = new MainWindowViewModel(
            service,
            toast,
            _ => new object(),
            (_, _) => new object(),
            _ => new object(),
            ServerSelectionFactory,
            themeRuntime: new StubThemeRuntime());

        var dispatcher = new AutomationDispatcher(mainViewModel);
        return new DispatcherFixture(dispatcher, serverViewModel);
    }

    private sealed class DispatcherFixture
    {
        public DispatcherFixture(AutomationDispatcher dispatcher, ServerSelectionViewModel serverViewModel)
        {
            Dispatcher = dispatcher;
            ServerViewModel = serverViewModel;
        }

        public AutomationDispatcher Dispatcher { get; }

        public ServerSelectionViewModel ServerViewModel { get; }

        public JsonDocument Dispatch(object payload)
        {
            var response = Dispatcher.Dispatch(JsonSerializer.Serialize(payload));
            return JsonDocument.Parse(response);
        }

        public void PopulateCatalog()
        {
            ServerViewModel.CatalogViewModel.LoadFromResponse(
                new ServerScanResponse
                {
                    Catalog = new[]
                    {
                        new ConfigCatalogEntry
                        {
                            ConfigId = "plugins",
                            DisplayName = "plugins.conf",
                            Format = "ini",
                            DriftCount = 1,
                            Severity = "medium",
                            PresentHosts = new[] { "App Inc" },
                            MissingHosts = Array.Empty<string>(),
                            CoverageStatus = "full",
                            LastUpdated = DateTimeOffset.UtcNow,
                        },
                    },
                },
                1);
        }

        public void PopulateDrilldown()
        {
            ServerViewModel.DrilldownViewModel = new ConfigDrilldownViewModel(new ConfigDrilldown
            {
                ConfigId = "plugins",
                DisplayName = "plugins.conf",
                Format = "ini",
                DriftCount = 1,
                BaselineHostId = "server01",
                DiffBefore = "before",
                DiffAfter = "after",
                UnifiedDiff = string.Empty,
                Servers = new[]
                {
                    new ConfigServerDetail
                    {
                        HostId = "server01",
                        Label = "App Inc",
                        Present = true,
                        IsBaseline = true,
                        Status = "Baseline",
                    },
                },
            });
        }
    }

    private sealed class StubThemeRuntime : IThemeRuntime
    {
        private static readonly ThemeOption s_default = new("dark-plus", "Dark+", ThemeVariant.Dark, "Palette.DarkPlus");

        public IReadOnlyList<ThemeOption> GetAvailableThemes() => new[] { s_default };

        public ThemeOption GetDefaultTheme(IReadOnlyList<ThemeOption> options) => options[0];

        public void ApplyTheme(ThemeOption option)
        {
        }
    }
}
#endif
