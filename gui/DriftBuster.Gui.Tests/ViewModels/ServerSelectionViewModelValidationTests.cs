using System.Collections.Generic;
using System.Linq;
using DriftBuster.Backend.Models;
using DriftBuster.Gui.Services;
using DriftBuster.Gui.Tests.Fakes;
using DriftBuster.Gui.ViewModels;
using FluentAssertions;
using Xunit;

namespace DriftBuster.Gui.Tests.ViewModels;

public sealed class ServerSelectionViewModelValidationTests
{
    [Fact]
    public void Root_input_error_overrides_root_states()
    {
        var viewModel = CreateViewModel();
        var slot = viewModel.Servers.First();

        slot.Scope = ServerScanScope.CustomRoots;
        slot.ReplaceRoots(new[] { new RootEntryViewModel("relative") });
        slot.ValidationSummary.Should().Be("Path must be absolute.");

        slot.RootInputError = "Absolute root path required.";
        slot.ValidationSummary.Should().Be("Absolute root path required.");
    }

    [Theory]
    [MemberData(nameof(PendingSummaryCases))]
    public void Pending_root_counts_are_reported(int count, string expected)
    {
        var viewModel = CreateViewModel();
        var slot = viewModel.Servers.First();

        slot.RootInputError = null;
        slot.IsEnabled = true;

        var pending = Enumerable.Range(0, count)
            .Select(index =>
            {
                var entry = new RootEntryViewModel($"C:/pending-{index}");
                entry.ValidationState = RootValidationState.Pending;
                entry.StatusMessage = string.Empty;
                return entry;
            })
            .ToList();

        slot.ReplaceRoots(pending);
        slot.ValidationSummary.Should().Be(expected);
    }

    [Fact]
    public void Disabled_servers_pause_validation_summary()
    {
        var viewModel = CreateViewModel();
        var slot = viewModel.Servers.First();

        slot.RootInputError = null;
        slot.IsEnabled = false;

        slot.ValidationSummary.Should().Be("Host disabled; validation paused.");
    }

    public static IEnumerable<object[]> PendingSummaryCases()
    {
        yield return new object[] { 1, "One root pending validation." };
        yield return new object[] { 3, "3 roots pending validation." };
    }

    private static ServerSelectionViewModel CreateViewModel()
    {
        var service = new FakeDriftbusterService();
        var toast = new ToastService(action => action());
        return new ServerSelectionViewModel(service, toast, new InMemorySessionCacheService());
    }
}
