using DriftBuster.Backend.Models;
using DriftBuster.Gui.Services;
using DriftBuster.Gui.Tests.Fakes;
using DriftBuster.Gui.ViewModels;

namespace DriftBuster.Gui.Tests.ViewModels;

/// <summary>Registry keys in Setup: every host's keys plus a host's own reach the plan, and the session keeps them.</summary>
public sealed class ServerSelectionRegistryTests
{
    [Fact]
    public async Task Shared_and_host_keys_reach_the_plan_and_the_session()
    {
        List<ServerScanPlan>? sent = null;
        var service = new FakeDriftbusterService
        {
            RunServerScansHandler = (plans, _, _) =>
            {
                sent = plans.ToList();
                return Task.FromResult(new ServerScanResponse
                {
                    Results = sent.Select(plan => new ServerScanResult { HostId = plan.HostId, Label = plan.Label, Status = ServerScanStatus.Succeeded }).ToArray(),
                });
            },
        };
        var cache = new InMemorySessionCacheService();
        using var viewModel = new ServerSelectionViewModel(service, new ToastService(action => action()), cache) { PersistSessionState = true };
        foreach (var server in viewModel.Servers.Skip(1))
        {
            server.IsEnabled = false;
        }

        var host = viewModel.Servers[0];
        host.IsEnabled = true;
        host.Scope = ServerScanScope.CustomRoots;
        host.ReplaceRoots([]);
        viewModel.NewSharedRegistryKey = @" HKLM\SOFTWARE\Vendor ";
        viewModel.AddSharedRegistryKeyCommand.Execute(null);
        viewModel.NewSharedRegistryKey = @"hklm\software\vendor";
        viewModel.AddSharedRegistryKeyCommand.Execute(null);
        viewModel.SelectedServer = host;
        host.NewRegistryKey = "Vendor Suite";
        viewModel.AddRegistryKeyCommand.Execute(host);
        host.Computer = "app-01";
        host.CredentialFile = @"C:\creds\app-01.xml";

        viewModel.SharedRegistryKeys.Should().Equal(@"HKLM\SOFTWARE\Vendor");
        host.SummaryText.Should().EndWith("registry on app-01");
        host.CredentialText.Should().Be("Signs in with app-01.xml");

        await viewModel.RunAllCommand.ExecuteAsync(null);

        var plan = sent.Should().ContainSingle("a registry-only host still runs").Subject;
        plan.Registry!.Keys.Should().Equal(@"HKLM\SOFTWARE\Vendor", "Vendor Suite");
        plan.Registry.Computer.Should().Be("app-01");
        plan.Registry.CredentialFile.Should().Be(@"C:\creds\app-01.xml");

        await viewModel.SaveSessionCommand.ExecuteAsync(null);
        cache.Snapshot!.SharedRegistryKeys.Should().Equal(@"HKLM\SOFTWARE\Vendor");
        var entry = cache.Snapshot.Servers.Single(saved => string.Equals(saved.HostId, host.HostId, StringComparison.Ordinal));
        entry.RegistryKeys.Should().Equal("Vendor Suite");
        entry.Computer.Should().Be("app-01");

        viewModel.RemoveRegistryKeyCommand.Execute("Vendor Suite");
        viewModel.RemoveSharedRegistryKeyCommand.Execute(@"HKLM\SOFTWARE\Vendor");
        host.RegistryKeys.Should().BeEmpty();
        viewModel.SharedRegistryKeys.Should().BeEmpty();
    }
}
