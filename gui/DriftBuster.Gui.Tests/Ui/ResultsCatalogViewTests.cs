using System;
using System.Linq;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;

using DriftBuster.Backend.Models;
using DriftBuster.Gui.Services;
using DriftBuster.Gui.ViewModels;
using DriftBuster.Gui.Views;

using FluentAssertions;

using Xunit;

namespace DriftBuster.Gui.Tests.Ui;

[Collection(HeadlessCollection.Name)]
public sealed class ResultsCatalogViewTests
{
    [AvaloniaFact]
    public void Virtualization_toggles_based_on_performance_profile()
    {
        var profile = new PerformanceProfile(virtualizationThreshold: 2);
        var viewModel = new ResultsCatalogViewModel(profile);
        viewModel.LoadFromResponse(BuildPartialResponse(2), totalHosts: 2);

        var view = new ResultsCatalogView
        {
            DataContext = viewModel,
        };

        view.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        view.Arrange(new Rect(view.DesiredSize));
        view.UpdateLayout();

        var virtualRepeater = view.FindControl<ItemsControl>("PartialCoverageVirtualRepeater");
        var fallback = view.FindControl<ItemsControl>("PartialCoverageFallback");

        virtualRepeater.Should().NotBeNull();
        fallback.Should().NotBeNull();

        virtualRepeater!.IsVisible.Should().BeTrue();
        fallback!.IsVisible.Should().BeFalse();

        viewModel.LoadFromResponse(BuildPartialResponse(0), totalHosts: 2);
        view.UpdateLayout();

        virtualRepeater.IsVisible.Should().BeFalse();
        fallback!.IsVisible.Should().BeTrue();

        viewModel.LoadFromResponse(BuildPartialResponse(1), totalHosts: 2);
        view.UpdateLayout();

        virtualRepeater.IsVisible.Should().BeFalse();
        fallback!.IsVisible.Should().BeTrue();
    }

    private static ServerScanResponse BuildPartialResponse(int count)
    {
        var entries = Enumerable.Range(0, count)
            .Select(index => new ConfigCatalogEntry
            {
                ConfigId = $"cfg-{index}",
                DisplayName = $"Config {index}",
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
