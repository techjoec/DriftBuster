using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;

using DriftBuster.Gui.Services;
using DriftBuster.Gui.Tests.Fakes;
using DriftBuster.Gui.ViewModels;
using DriftBuster.Gui.Views;

using FluentAssertions;
using Xunit;

namespace DriftBuster.Gui.Tests.Ui;

[Collection(HeadlessCollection.Name)]
public sealed class DiffViewTests
{
    [AvaloniaFact]
    public void Should_Create_DiffView_With_Default_ViewModel()
    {
        var view = new DiffView
        {
            DataContext = new DiffViewModel(new FakeDriftbusterService()),
        };

        view.DataContext.Should().BeOfType<DiffViewModel>();
    }

    [AvaloniaFact]
    public async Task MruSelector_applies_selected_entry()
    {
        using var temp = new TempDirectory();
        var baseline = temp.CreateFile("baseline.json", "{}");
        var comparison = temp.CreateFile("comparison.json", "{}");

        var store = new DiffPlannerMruStore(temp.Path);
        var snapshot = new DiffPlannerMruSnapshot
        {
            Entries = new List<DiffPlannerMruEntry>
            {
                new()
                {
                    BaselinePath = baseline,
                    ComparisonPaths = new List<string> { comparison },
                    DisplayName = "Baseline vs comparison",
                    PayloadKind = DiffPlannerPayloadKind.Sanitized,
                    LastUsedUtc = DateTimeOffset.UtcNow,
                },
            },
        };

        await store.SaveAsync(snapshot);

        var viewModel = new DiffViewModel(new FakeDriftbusterService(), store);
        await viewModel.Initialization;

        var view = new DiffView
        {
            DataContext = viewModel,
        };

        var selector = FindByAutomationId<ComboBox>(view, "DiffPlanner.MruSelector");
        selector.Items.Should().NotBeNull();

        selector.SelectedIndex = 0;

        viewModel.Inputs[0].Path.Should().Be(baseline);
        viewModel.Inputs[1].Path.Should().Be(comparison);
    }

    [AvaloniaFact]
    public void Json_toggle_updates_panes_and_copy_state()
    {
        var viewModel = new DiffViewModel(new FakeDriftbusterService())
        {
            RawJson = "{\"raw\":true}",
            SanitizedJson = "{\"safe\":true}",
        };

        viewModel.JsonViewMode = DiffViewModel.DiffJsonViewMode.Sanitized;

        var view = new DiffView
        {
            DataContext = viewModel,
        };

        var sanitizedPane = FindByAutomationId<Border>(view, "DiffPlanner.JsonPane.Sanitized");
        var rawPane = FindByAutomationId<Border>(view, "DiffPlanner.JsonPane.Raw");
        var copyButton = FindByAutomationId<Button>(view, "DiffPlanner.CopyJsonButton");
        var toggleList = view.FindControl<ItemsControl>("JsonViewToggleList");
        toggleList.Should().NotBeNull();
        toggleList!.Items.Cast<object>().Should().HaveCount(2);

        sanitizedPane.IsVisible.Should().BeTrue();
        rawPane.IsVisible.Should().BeFalse();
        copyButton.IsEnabled.Should().BeTrue();

        viewModel.SelectJsonViewModeCommand.Execute(DiffViewModel.DiffJsonViewMode.Raw);

        rawPane.IsVisible.Should().BeTrue();
        sanitizedPane.IsVisible.Should().BeFalse();
        copyButton.IsEnabled.Should().BeFalse();

        viewModel.SelectJsonViewModeCommand.Execute(DiffViewModel.DiffJsonViewMode.Sanitized);

        sanitizedPane.IsVisible.Should().BeTrue();
        rawPane.IsVisible.Should().BeFalse();
        copyButton.IsEnabled.Should().BeTrue();
    }

    private static T FindByAutomationId<T>(Control root, string automationId)
        where T : Control
    {
        return root
            .GetLogicalDescendants()
            .OfType<T>()
            .First(control => AutomationProperties.GetAutomationId(control) == automationId);
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string CreateFile(string name, string contents)
        {
            var filePath = System.IO.Path.Combine(Path, name);
            File.WriteAllText(filePath, contents);
            return filePath;
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch
            {
                // Ignore cleanup failures in tests.
            }
        }
    }
}
