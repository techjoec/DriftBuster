using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using AwesomeAssertions;
using DriftBuster.Backend.Models;
using DriftBuster.Gui.Services;
using DriftBuster.Gui.Tests.Fakes;
using DriftBuster.Gui.ViewModels;
using DriftBuster.Gui.Views;
using Xunit;

namespace DriftBuster.Gui.Tests.Ui;

/// <summary>
/// Guards the UI Automation surface: every interactive control in every view must carry an
/// AutomationId and resolve an accessible name so Windows-MCP and Appium-style drivers can address it.
/// The headless host does not load the Fluent theme, so item templates are built directly against a
/// sample item instead of relying on layout to realise them.
/// </summary>
[Collection(HeadlessCollection.Name)]
public sealed class AutomationPropertiesTests
{
    public static TheoryData<string> Views => new()
    {
        nameof(MainWindow),
        nameof(ServerSelectionView),
        nameof(DiffView),
        nameof(HuntView),
        nameof(RunProfilesView),
        nameof(ConfigDrilldownView),
        nameof(ResultsCatalogView),
        nameof(SecretScannerSettingsWindow),
        nameof(ToastHost),
    };

    [AvaloniaTheory]
    [MemberData(nameof(Views))]
    public void Interactive_controls_expose_automation_id_and_name(string viewName)
    {
        HeadlessFixture.EnsureFonts();

        var root = CreateView(viewName);
        var interactive = CollectControls(root)
            .Where(entry => IsInteractive(entry.Control))
            .Where(entry => entry.Control.TemplatedParent is null)
            .ToList();

        interactive.Should().NotBeEmpty($"{viewName} should expose at least one interactive control");

        var problems = new List<string>();
        foreach (var (control, hasData) in interactive)
        {
            var automationId = AutomationProperties.GetAutomationId(control);
            if (string.IsNullOrWhiteSpace(automationId))
            {
                problems.Add($"{viewName}: {Describe(control, automationId)} has no AutomationId");
            }

            // A template built without a bound item cannot resolve data-bound names.
            if (hasData && string.IsNullOrWhiteSpace(ResolveName(control)))
            {
                problems.Add($"{viewName}: {Describe(control, automationId)} has no accessible name");
            }
        }

        problems.Should().BeEmpty(string.Join(Environment.NewLine, problems));
    }

    private static bool IsInteractive(Control control)
    {
        return control is Button
            or TextBox
            or AutoCompleteBox
            or ComboBox
            or Slider
            or ListBox
            or DataGrid
            or TabControl
            or TabItem
            or MenuItem
            or Expander
            or TreeView;
    }

    /// <summary>
    /// Walks the logical tree and, for every item/content/cell template it meets, builds the template
    /// against the first bound item so controls declared inside DataTemplates are inspected too.
    /// Templates whose source list is empty are still built (with no item) so their IDs are checked.
    /// </summary>
    private static IEnumerable<(Control Control, bool HasData)> CollectControls(Control root)
    {
        var visited = new HashSet<Control>();
        var pending = new Queue<(Control Root, bool HasData)>();
        pending.Enqueue((root, true));

        while (pending.Count > 0)
        {
            var (current, hasData) = pending.Dequeue();
            foreach (var control in current.GetSelfAndLogicalDescendants().OfType<Control>())
            {
                if (!visited.Add(control))
                {
                    continue;
                }

                yield return (control, hasData);

                foreach (var (template, item) in TemplatesOf(control))
                {
                    var built = template.Build(item);
                    if (built is null)
                    {
                        continue;
                    }

                    built.DataContext = item;
                    pending.Enqueue((built, hasData && item is not null));
                }
            }
        }
    }

    private static IEnumerable<(IDataTemplate Template, object? Item)> TemplatesOf(Control control)
    {
        switch (control)
        {
            case DataGrid grid:
                var row = grid.ItemsSource?.Cast<object>().FirstOrDefault();
                foreach (var column in grid.Columns.OfType<DataGridTemplateColumn>())
                {
                    if (column.CellTemplate is { } cellTemplate)
                    {
                        yield return (cellTemplate, row);
                    }
                }

                break;
            case ItemsControl items when items.ItemTemplate is { } itemTemplate:
                yield return (itemTemplate, items.Items.Cast<object>().FirstOrDefault());
                break;
            case ContentControl content when content.ContentTemplate is { } contentTemplate:
                yield return (contentTemplate, content.Content);
                break;
        }
    }

    private static string? ResolveName(Control control)
    {
        var peer = ControlAutomationPeer.CreatePeerForElement(control);
        var name = peer?.GetName();
        if (!string.IsNullOrWhiteSpace(name))
        {
            return name;
        }

        name = AutomationProperties.GetName(control);
        if (!string.IsNullOrWhiteSpace(name))
        {
            return name;
        }

        // Without the Fluent theme no templates are applied, so fall back to the text content the
        // peer would derive the name from once the control is rendered.
        return control switch
        {
            HeaderedContentControl headered when headered.Header is string header => header,
            ContentControl content when content.Content is string text => text,
            _ => null,
        };
    }

    private static string Describe(Control control, string? automationId)
    {
        var name = string.IsNullOrEmpty(control.Name) ? "-" : control.Name;
        var id = string.IsNullOrEmpty(automationId) ? "-" : automationId;
        var text = control switch
        {
            HeaderedContentControl headered when headered.Header is string header => header,
            ContentControl content when content.Content is string value => value,
            TextBox textBox => textBox.Watermark ?? string.Empty,
            _ => string.Empty,
        };
        return $"{control.GetType().Name} (x:Name={name}, AutomationId={id}, text=\"{text}\")";
    }

    private static Control CreateView(string viewName)
    {
        var service = new FakeDriftbusterService();
        return viewName switch
        {
            nameof(MainWindow) => CreateMainWindow(service),
            nameof(ServerSelectionView) => new ServerSelectionView { DataContext = CreateServerSelectionViewModel(service) },
            nameof(DiffView) => new DiffView { DataContext = new DiffViewModel(service) },
            nameof(HuntView) => new HuntView { DataContext = new HuntViewModel(service) },
            nameof(RunProfilesView) => new RunProfilesView { DataContext = CreateRunProfilesViewModel(service) },
            nameof(ConfigDrilldownView) => new ConfigDrilldownView { DataContext = new ConfigDrilldownViewModel(BuildDrilldown()) },
            nameof(ResultsCatalogView) => new ResultsCatalogView { DataContext = CreateCatalogViewModel() },
            nameof(SecretScannerSettingsWindow) => new SecretScannerSettingsWindow { DataContext = CreateSecretScannerViewModel() },
            nameof(ToastHost) => new ToastHost { DataContext = CreateToastService() },
            _ => throw new ArgumentOutOfRangeException(nameof(viewName), viewName, "Unknown view"),
        };
    }

    private static MainWindow CreateMainWindow(FakeDriftbusterService service)
    {
        var toasts = new ToastService(action => action());
        var window = new MainWindow { DataContext = new MainWindowViewModel(service, toasts) };

        // Shown after the view model's own startup toast so the sample with both actions is the first item.
        ShowSampleToast(toasts);
        return window;
    }

    private static ServerSelectionViewModel CreateServerSelectionViewModel(FakeDriftbusterService service)
    {
        var viewModel = new ServerSelectionViewModel(service, new ToastService(), new InMemorySessionCacheService());
        var server = viewModel.Servers[0];
        server.Scope = ServerScanScope.CustomRoots;
        server.NewRootPath = Path.GetTempPath();
        viewModel.AddRootCommand.Execute(server);
        return viewModel;
    }

    private static RunProfilesViewModel CreateRunProfilesViewModel(FakeDriftbusterService service)
    {
        var viewModel = new RunProfilesViewModel(service);
        viewModel.AddOptionCommand.Execute(null);
        viewModel.AddScheduleCommand.Execute(null);
        viewModel.Schedules[0].AddMetadataCommand.Execute(null);
        return viewModel;
    }

    private static ResultsCatalogViewModel CreateCatalogViewModel()
    {
        var viewModel = new ResultsCatalogViewModel();
        var entries = Enumerable.Range(0, 2)
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
        viewModel.LoadFromResponse(new ServerScanResponse { Catalog = entries }, totalHosts: 2);
        return viewModel;
    }

    private static SecretScannerSettingsViewModel CreateSecretScannerViewModel()
    {
        return new SecretScannerSettingsViewModel(new SecretScannerOptions
        {
            IgnoreRules = new[] { "rule-one" },
            IgnorePatterns = new[] { "pattern" },
        });
    }

    private static ConfigDrilldown BuildDrilldown()
    {
        return new ConfigDrilldown
        {
            ConfigId = "appsettings",
            DisplayName = "appsettings.json",
            Format = "json",
            DiffBefore = "{}",
            DiffAfter = "{}",
            BaselineHostId = "server01",
            LastUpdated = DateTimeOffset.UtcNow,
            Servers = new[]
            {
                new ConfigServerDetail { HostId = "server01", Label = "Baseline", Present = true, IsBaseline = true, Status = "ok" },
                new ConfigServerDetail { HostId = "server02", Label = "Drift", Present = true, Status = "drift" },
            },
        };
    }

    private static ToastService CreateToastService()
    {
        var service = new ToastService(action => action());
        ShowSampleToast(service);
        return service;
    }

    private static void ShowSampleToast(ToastService service)
    {
        service.Show(
            "Title",
            "Message",
            ToastLevel.Info,
            TimeSpan.FromMinutes(5),
            new ToastAction("Open", () => Task.CompletedTask),
            new ToastAction("Later", () => Task.CompletedTask));
    }
}
