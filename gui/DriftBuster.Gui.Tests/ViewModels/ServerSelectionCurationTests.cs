using DriftBuster.Backend.Curation;
using DriftBuster.Backend.Models;
using DriftBuster.Gui.Services;
using DriftBuster.Gui.Tests.Fakes;
using DriftBuster.Gui.ViewModels;

namespace DriftBuster.Gui.Tests.ViewModels;

/// <summary>A Multi-server run hands its comparison to curation: host set, scan history, and each server's text for Raw data.</summary>
public sealed class ServerSelectionCurationTests
{
    [Fact]
    public async Task A_run_records_history_for_its_host_set_and_serves_raw_text()
    {
        var curation = new InMemoryCurationService();
        List<ServerScanPlan>? sent = null;
        var service = new FakeDriftbusterService
        {
            RunServerScansHandler = (plans, _, _) =>
            {
                sent = plans.ToList();
                return Task.FromResult(new ServerScanResponse
                {
                    Results = sent.Select(plan => new ServerScanResult { HostId = plan.HostId, Label = plan.Label, Status = ServerScanStatus.Succeeded }).ToArray(),
                    Comparison = SampleComparison.Build(),
                    Drilldown =
                    [
                        new ConfigDrilldown
                        {
                            ConfigId = "json/app",
                            BaselineHostId = "a",
                            DiffBefore = "baseline text",
                            HostDiffs = [new ConfigHostDiff { HostId = "c", After = "prod text" }],
                        },
                    ],
                });
            },
        };
        using var viewModel = new ServerSelectionViewModel(service, new ToastService(action => action()), new InMemorySessionCacheService(), curation: curation);
        viewModel.Servers[0].IsEnabled = true;

        await viewModel.RunAllCommand.ExecuteAsync(null);

        sent.Should().NotBeNull();
        var hostSet = CurationScopes.HostSetId(sent!.Select(DriftBuster.Backend.MultiServer.MultiServerPlan.FromServerScanPlan));
        curation.Recorded.Should().ContainSingle().Which.HostSetId.Should().Be(hostSet);
        viewModel.CompareViewModel.HostSetId.Should().Be(hostSet);
        var file = viewModel.CompareViewModel.VisibleFiles.First(candidate => string.Equals(candidate.ConfigId, "json/app", StringComparison.Ordinal));
        var context = new CompareContext(file);
        viewModel.CompareViewModel.RawText(context, "a").Should().Be("baseline text");
        viewModel.CompareViewModel.RawText(context, "c").Should().Be("prod text");
        viewModel.CompareViewModel.RawText(context, "b").Should().BeNull();
    }

    [Fact]
    public async Task Files_hands_a_file_to_Compare()
    {
        var service = new FakeDriftbusterService
        {
            RunServerScansHandler = (plans, _, _) => Task.FromResult(new ServerScanResponse
            {
                Results = plans.Select(plan => new ServerScanResult { HostId = plan.HostId, Label = plan.Label, Status = ServerScanStatus.Succeeded }).ToArray(),
                Comparison = SampleComparison.Build(),
            }),
        };
        using var viewModel = new ServerSelectionViewModel(service, new ToastService(action => action()), new InMemorySessionCacheService(), curation: new InMemoryCurationService());
        viewModel.Servers[0].IsEnabled = true;
        await viewModel.RunAllCommand.ExecuteAsync(null);
        viewModel.CatalogViewModel.Compare.Should().BeSameAs(viewModel.CompareViewModel);
        viewModel.CurrentView = MultiServerView.Details;

        viewModel.CatalogViewModel.RequestCompare(new ConfigCatalogItemViewModel(new ConfigCatalogEntry { ConfigId = "json/same", DisplayName = "same.json" }, totalHosts: 3));

        viewModel.CurrentView.Should().Be(MultiServerView.Compare);
        viewModel.CompareViewModel.SelectedFile!.Path.Should().Be("same.json");
    }
}
