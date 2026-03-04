using System;
using System.Linq;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using AwesomeAssertions;
using DriftBuster.Backend.Models;
using DriftBuster.Gui.Services;
using DriftBuster.Gui.ViewModels;
using DriftBuster.Gui.Views;
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

    [AvaloniaFact]
    public void Catalog_grid_sorting_updates_sort_descriptor_and_toggles_direction()
    {
        var viewModel = new ResultsCatalogViewModel();
        viewModel.LoadFromResponse(BuildPartialResponse(2), totalHosts: 2);
        var view = new ResultsCatalogView
        {
            DataContext = viewModel,
        };

        var column = new DataGridTextColumn
        {
            Header = CatalogSortColumns.Config,
            SortMemberPath = CatalogSortColumns.Config,
        };
        var args = CreateColumnEventArgs(column);

        InvokeCatalogSorting(view, args);

        viewModel.SortDescriptor.ColumnKey.Should().Be(CatalogSortColumns.Config);
        viewModel.SortDescriptor.Descending.Should().BeFalse();
        args.Handled.Should().BeTrue();

        var secondArgs = CreateColumnEventArgs(column);
        InvokeCatalogSorting(view, secondArgs);
        viewModel.SortDescriptor.Descending.Should().BeTrue();
        secondArgs.Handled.Should().BeTrue();
    }

    [AvaloniaFact]
    public void Catalog_grid_sorting_ignores_non_catalog_datacontext()
    {
        var view = new ResultsCatalogView
        {
            DataContext = new object(),
        };
        var column = new DataGridTextColumn
        {
            Header = CatalogSortColumns.Config,
            SortMemberPath = CatalogSortColumns.Config,
        };
        var args = CreateColumnEventArgs(column);

        InvokeCatalogSorting(view, args);

        args.Handled.Should().BeFalse();
    }

    private static DataGridColumnEventArgs CreateColumnEventArgs(DataGridColumn column)
    {
        var args = Activator.CreateInstance(
            typeof(DataGridColumnEventArgs),
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: new object?[] { column },
            culture: null) as DataGridColumnEventArgs;

        args.Should().NotBeNull();
        return args!;
    }

    private static void InvokeCatalogSorting(ResultsCatalogView view, DataGridColumnEventArgs args)
    {
        var method = typeof(ResultsCatalogView).GetMethod("OnCatalogGridSorting", BindingFlags.Instance | BindingFlags.NonPublic);
        method.Should().NotBeNull();
        method!.Invoke(view, new object?[] { null, args });
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
