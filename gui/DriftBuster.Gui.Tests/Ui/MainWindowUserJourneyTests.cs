using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using CommunityToolkit.Mvvm.Input;
using DriftBuster.Backend.Models;
using DriftBuster.Gui.Services;
using DriftBuster.Gui.Tests.Fakes;
using DriftBuster.Gui.ViewModels;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace DriftBuster.Gui.Tests.Ui;

[Collection(HeadlessCollection.Name)]
public sealed class MainWindowUserJourneyTests
{
    private readonly ITestOutputHelper _output;

    public MainWindowUserJourneyTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [AvaloniaFact]
    public async Task User_flow_covers_main_sections_and_multi_server_results()
    {
        var exported = new List<ConfigDrilldownExportRequest>();
        var huntResponse = BuildHuntResponse();

        var service = new FakeDriftbusterService
        {
            PingAsyncHandler = _ => Task.FromResult("pong"),
            HuntResponse = huntResponse,
            RunServerScansHandler = async (plans, progress, token) =>
            {
                var list = plans.ToList();
                await Task.Yield();
                return BuildScanResponse(list);
            },
        };

        var toastService = new ToastService(action => action());

        ServerSelectionViewModel? serverSelection = null;
        HuntViewModel? huntViewModel = null;
        RunProfilesViewModel? runProfilesViewModel = null;
        DiffViewModel? diffViewModel = null;

        var main = new MainWindowViewModel(
            service,
            toastService,
            diffViewFactory: s =>
            {
                diffViewModel = new DiffViewModel(s);
                return diffViewModel;
            },
            huntViewFactory: (s, initial) =>
            {
                huntViewModel = new HuntViewModel(s, initial);
                return huntViewModel;
            },
            profilesViewFactory: s =>
            {
                runProfilesViewModel = new RunProfilesViewModel(s);
                return runProfilesViewModel;
            },
            serverSelectionFactory: (s, toast) =>
            {
                serverSelection = new ServerSelectionViewModel(s, toast);
                foreach (var slot in serverSelection.Servers.Where(slot => slot.IsEnabled))
                {
                    slot.ReplaceRoots(new[]
                    {
                        new RootEntryViewModel(AppContext.BaseDirectory)
                    });
                }

                serverSelection.ExportCallback = request =>
                {
                    exported.Add(request);
                    return Task.CompletedTask;
                };

                return serverSelection;
            });

        // Multi-server navigation + run
        main.ShowMultiServer();
        main.ActiveView.Should().Be(MainWindowViewModel.MainViewSection.MultiServer);
        serverSelection.Should().NotBeNull();

        await serverSelection!.RunAllCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => !serverSelection.IsBusy);
        serverSelection.CatalogViewModel.HasEntries.Should().BeTrue();
        serverSelection.IsViewingCatalog.Should().BeTrue();
        serverSelection.FilteredActivityEntries.Should().NotBeEmpty();

        var catalogEntry = serverSelection.CatalogViewModel.FilteredEntries.First();
        serverSelection.CatalogViewModel.DrilldownCommand.Execute(catalogEntry);
        serverSelection.DrilldownViewModel.Should().NotBeNull();

        var drilldown = serverSelection.DrilldownViewModel!;
        drilldown.SelectNoneCommand.Execute(null);
        drilldown.SelectAllCommand.Execute(null);
        drilldown.ReScanSelectedCommand.Execute(null);
        await WaitUntilAsync(() => !serverSelection.IsBusy);

        drilldown = serverSelection.DrilldownViewModel!;
        drilldown.ExportHtmlCommand.CanExecute(null).Should().BeTrue();
        await drilldown.ExportHtmlCommand.ExecuteAsync(null);
        exported.Should().ContainSingle(req => req.Format == ConfigDrilldownViewModel.ExportFormat.Html);

        drilldown.ReScanSelectedCommand.Execute(null);
        await WaitUntilAsync(() => !serverSelection.IsBusy);
        drilldown = serverSelection.DrilldownViewModel!;
        drilldown.BackCommand.Execute(null);
        serverSelection.IsViewingCatalog.Should().BeTrue();

        serverSelection.ShowSetupCommand.Execute(null);
        serverSelection.IsViewingCatalog.Should().BeFalse();
        serverSelection.ShowCatalogCommand.Execute(null);
        serverSelection.IsViewingCatalog.Should().BeTrue();
        serverSelection.ShowDrilldownCommand.CanExecute(null).Should().BeTrue();

        serverSelection.CopyActivityCommand.CanExecute(serverSelection.FilteredActivityEntries.First()).Should().BeTrue();
        serverSelection.CopyActivityCommand.Execute(serverSelection.FilteredActivityEntries.First());

        toastService.ActiveToasts.Should().NotBeEmpty();

        // Hunt flow via main window
        main.ShowHunt();
        main.ActiveView.Should().Be(MainWindowViewModel.MainViewSection.Hunt);
        huntViewModel.Should().NotBeNull();
        huntViewModel!.DirectoryPath = AppContext.BaseDirectory;
        huntViewModel.Pattern = "server";
        await huntViewModel.RunHuntCommand.ExecuteAsync(null);
        huntViewModel.HasHits.Should().BeTrue();
        huntViewModel.RawJson.Should().Be(huntResponse.RawJson);

        // Profiles flow
        main.ShowProfiles();
        main.ActiveView.Should().Be(MainWindowViewModel.MainViewSection.Profiles);
        runProfilesViewModel.Should().NotBeNull();
        runProfilesViewModel!.ProfileName = "demo";
        runProfilesViewModel.Sources[0].Path = AppContext.BaseDirectory;
        runProfilesViewModel.SaveCommand.CanExecute(null).Should().BeTrue();
        runProfilesViewModel.SecretScanner = new SecretScannerOptions
        {
            IgnoreRules = new[] { "rule" },
            IgnorePatterns = new[] { "pattern" },
        };
        runProfilesViewModel.ApplySecretScanner(runProfilesViewModel.SecretScanner);

        // Diff and health
        main.ShowDiff();
        main.ActiveView.Should().Be(MainWindowViewModel.MainViewSection.Diff);
        diffViewModel.Should().NotBeNull();

        await main.PingCoreCommand.ExecuteAsync(null);
        await main.CheckHealthCommand.ExecuteAsync(null);
        main.IsBackendHealthy.Should().BeTrue();
        main.BackendStatusText.Should().Contain("Core OK");

        toastService.ActiveToasts.Should().NotBeEmpty();
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, int timeoutMs = 3000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (predicate())
            {
                return;
            }

            await Task.Delay(25);
        }

        predicate().Should().BeTrue("expected condition to be satisfied within timeout");
    }

    private static HuntResult BuildHuntResponse()
    {
        return new HuntResult
        {
            Directory = "configs",
            Pattern = "server",
            Count = 1,
            RawJson = "{\"count\":1}",
            Hits = new[]
            {
                new HuntHit
                {
                    RelativePath = "configs/appsettings.json",
                    Path = "/configs/appsettings.json",
                    LineNumber = 12,
                    Excerpt = "server: host",
                    Rule = new HuntRuleSummary
                    {
                        Name = "server-name",
                        Description = "Potential server names",
                        TokenName = "server",
                    },
                },
            },
        };
    }

    private static ServerScanResponse BuildScanResponse(IReadOnlyList<ServerScanPlan> plans)
    {
        var labels = plans.Select(plan => string.IsNullOrWhiteSpace(plan.Label) ? plan.HostId : plan.Label).ToArray();
        var results = plans.Select(plan => new ServerScanResult
        {
            HostId = plan.HostId,
            Label = plan.Label,
            Status = ServerScanStatus.Succeeded,
            Message = "Completed",
            Timestamp = DateTimeOffset.UtcNow,
            Availability = ServerAvailabilityStatus.Found,
        }).ToArray();

        var catalog = new[]
        {
            new ConfigCatalogEntry
            {
                ConfigId = "appsettings.json",
                DisplayName = "appsettings.json",
                Format = "json",
                Severity = "high",
                DriftCount = 2,
                PresentHosts = labels,
                MissingHosts = Array.Empty<string>(),
                CoverageStatus = "full",
                LastUpdated = DateTimeOffset.UtcNow,
                HasMaskedTokens = true,
                HasSecrets = true,
            },
        };

        var drilldown = new[]
        {
            new ConfigDrilldown
            {
                ConfigId = "appsettings.json",
                DisplayName = "appsettings.json",
                Format = "json",
                BaselineHostId = plans.First().HostId,
                DiffBefore = "{\"LogLevel\":\"Information\"}",
                DiffAfter = "{\"LogLevel\":\"Warning\"}",
                UnifiedDiff = "- LogLevel: Information\n+ LogLevel: Warning",
                DriftCount = 2,
                LastUpdated = DateTimeOffset.UtcNow,
                HasMaskedTokens = true,
                HasSecrets = true,
                Notes = new[] { "Investigate log level drift" },
                Servers = plans.Select((plan, index) => new ConfigServerDetail
                {
                    HostId = plan.HostId,
                    Label = string.IsNullOrWhiteSpace(plan.Label) ? plan.HostId : plan.Label,
                    Present = true,
                    IsBaseline = index == 0,
                    Status = index == 0 ? "Baseline" : "Drift",
                    DriftLineCount = index + 1,
                    RedactionStatus = index % 2 == 0 ? "Masked" : "Visible",
                    Masked = index % 2 == 0,
                    HasSecrets = index % 2 == 1,
                    LastSeen = DateTimeOffset.UtcNow,
                    PresenceStatus = ConfigPresenceStatus.Found,
                }).ToArray(),
            },
        };

        return new ServerScanResponse
        {
            Results = results,
            Catalog = catalog,
            Drilldown = drilldown,
        };
    }
}
