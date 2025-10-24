using System;
using System.Linq;
using DriftBuster.Backend.Models;
using DriftBuster.Gui.Services;
using DriftBuster.Gui.ViewModels;
using FluentAssertions;
using Xunit;

namespace DriftBuster.Gui.Tests.ViewModels;

public sealed class ResultsCatalogViewModelTests
{
    private static ServerScanResponse BuildResponse()
    {
        return new ServerScanResponse
        {
            Catalog = new[]
            {
                new ConfigCatalogEntry
                {
                    ConfigId = "appsettings",
                    DisplayName = "appsettings.json",
                    Format = "json",
                    DriftCount = 2,
                    Severity = "high",
                    PresentHosts = new[] { "server01", "server02" },
                    MissingHosts = Array.Empty<string>(),
                    CoverageStatus = "full",
                    HasSecrets = true,
                    HasMaskedTokens = true,
                    LastUpdated = DateTimeOffset.UtcNow,
                },
                new ConfigCatalogEntry
                {
                    ConfigId = "plugins",
                    DisplayName = "plugins.conf",
                    Format = "ini",
                    DriftCount = 0,
                    Severity = "low",
                    PresentHosts = new[] { "server01" },
                    MissingHosts = new[] { "server02" },
                    CoverageStatus = "partial",
                    LastUpdated = DateTimeOffset.UtcNow.AddMinutes(-5),
                },
            },
        };
    }

    [Fact]
    public void Filters_by_format_severity_and_text()
    {
        var viewModel = new ResultsCatalogViewModel();
        viewModel.LoadFromResponse(BuildResponse(), totalHosts: 2);

        viewModel.FilteredEntries.Should().HaveCount(2);
        viewModel.FormatOptions.Should().Contain(new[] { "Any", "json", "ini" });

        viewModel.SelectedFormat = "json";
        viewModel.FilteredEntries.Should().HaveCount(1);
        viewModel.FilteredEntries.Single().DisplayName.Should().Contain("appsettings");

        viewModel.SelectedSeverityFilter = SeverityFilterOption.High;
        viewModel.FilteredEntries.Should().HaveCount(1);

        viewModel.SelectedCoverageFilter = CoverageFilterOption.Full;
        viewModel.FilteredEntries.Should().HaveCount(1);

        viewModel.SearchText = "plugins";
        viewModel.FilteredEntries.Should().BeEmpty();

        viewModel.SelectedFormat = "Any";
        viewModel.SelectedCoverageFilter = CoverageFilterOption.Partial;
        viewModel.SelectedSeverityFilter = SeverityFilterOption.Any;
        viewModel.SearchText = string.Empty;
        viewModel.FilteredEntries.Should().HaveCount(1);
        viewModel.FilteredEntries.Single().DisplayName.Should().Contain("plugins");
    }

    [Fact]
    public void Raises_events_for_drilldown_and_rescan()
    {
        var viewModel = new ResultsCatalogViewModel();
        viewModel.LoadFromResponse(BuildResponse(), totalHosts: 2);
        var target = viewModel.FilteredEntries.First();

        ConfigCatalogItemViewModel? drilldownItem = null;
        viewModel.DrilldownRequested += (_, entry) => drilldownItem = entry;
        viewModel.DrilldownCommand.Execute(target);
        drilldownItem.Should().BeSameAs(target);

        string[]? hosts = null;
        viewModel.ReScanRequested += (_, missing) => hosts = missing.ToArray();
        var partial = viewModel.FilteredEntries.Single(entry => entry.DisplayName.Contains("plugins"));
        viewModel.ReScanMissingCommand.Execute(partial);
        hosts.Should().NotBeNull();
        hosts!.Should().Contain("server02");

        hosts = null;
        viewModel.ReScanAllPartialCommand.Execute(null);
        hosts.Should().NotBeNull();
        hosts!.Should().Contain("server02");

    }

    [Fact]
    public void Resets_and_rebuilds_format_options()
    {
        var viewModel = new ResultsCatalogViewModel();
        viewModel.LoadFromResponse(BuildResponse(), totalHosts: 2);
        viewModel.SelectedFormat = "ini";
        viewModel.Reset();

        viewModel.SelectedFormat.Should().Be("Any");
        viewModel.CoverageFilterOptions.Should().NotBeNull();
        viewModel.FormatOptions.Should().Contain("Any");
        viewModel.FilteredEntries.Should().BeEmpty();
        viewModel.HasEntries.Should().BeFalse();
    }

    [Fact]
    public void Virtualization_follows_performance_profile()
    {
        var profile = new PerformanceProfile(virtualizationThreshold: 2);
        var viewModel = new ResultsCatalogViewModel(profile);

        var partialResponse = BuildPartialResponse(count: 3);
        viewModel.LoadFromResponse(partialResponse, totalHosts: 2);

        viewModel.UseVirtualizedPartialCoverage.Should().BeTrue();

        var minimalResponse = BuildPartialResponse(count: 1);
        viewModel.LoadFromResponse(minimalResponse, totalHosts: 2);

        viewModel.UseVirtualizedPartialCoverage.Should().BeFalse();
    }

    private static ServerScanResponse BuildPartialResponse(int count)
    {
        var entries = Enumerable.Range(0, count)
            .Select(index => new ConfigCatalogEntry
            {
                ConfigId = $"config-{index}",
                DisplayName = $"config-{index}",
                Format = "json",
                DriftCount = 0,
                Severity = "low",
                PresentHosts = new[] { "server01" },
                MissingHosts = new[] { "server02" },
                CoverageStatus = "partial",
                LastUpdated = DateTimeOffset.UtcNow,
            })
            .ToArray();

        return new ServerScanResponse
        {
            Catalog = entries,
        };
    }
}
