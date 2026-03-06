using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using DriftBuster.Backend.Models;
using DriftBuster.Gui.Services;
using DriftBuster.Gui.Tests.Fakes;
using DriftBuster.Gui.ViewModels;
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
        slot.Scope = ServerScanScope.CustomRoots;

        var pending = Enumerable.Range(0, count)
            .Select(index => new RootEntryViewModel($"C:/pending-{index}"))
            .ToList();

        slot.ReplaceRoots(pending);
        foreach (var entry in slot.Roots)
        {
            entry.ValidationState = RootValidationState.Pending;
            entry.StatusMessage = string.Empty;
        }

        slot.RefreshValidationSummary();
        slot.ValidationSummary.Should().Be(expected);
    }

    [Fact]
    public void Non_custom_scope_reports_roots_as_ignored()
    {
        var viewModel = CreateViewModel();
        var slot = viewModel.Servers.First();

        slot.IsEnabled = true;
        slot.Scope = ServerScanScope.AllDrives;
        slot.ReplaceRoots(new[]
        {
            new RootEntryViewModel("C:\\any")
            {
                ValidationState = RootValidationState.Pending,
            },
        });

        slot.ValidationSummary.Should().Be("Roots ignored for this scope.");
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
