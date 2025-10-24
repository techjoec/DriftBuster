using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;

using Avalonia.Controls;
using Avalonia.Interactivity;

using DriftBuster.Gui.Tests.Fakes;
using DriftBuster.Gui.ViewModels;
using DriftBuster.Gui.Views;

namespace DriftBuster.Gui.Tests.Ui;

[Collection(HeadlessCollection.Name)]
public sealed class DiffViewInteractionTests
{
    [AvaloniaFact]
    public void BrowseFile_without_storage_provider_leaves_path_unset()
    {
        var viewModel = new DiffViewModel(new FakeDriftbusterService());
        var view = new DiffView
        {
            DataContext = viewModel,
        };

        var input = viewModel.Inputs[0];
        input.Path.Should().BeNull();

        Invoke(view, "OnBrowseFile", new Button { Tag = input }, new RoutedEventArgs(Button.ClickEvent));

        input.Path.Should().BeNull();
    }

    [AvaloniaFact]
    public void BrowseFile_with_override_updates_path()
    {
        var viewModel = new DiffViewModel(new FakeDriftbusterService());
        var view = new DiffView
        {
            DataContext = viewModel,
            FilePickerOverride = () => Task.FromResult<string?>("/tmp/sample.json"),
        };

        var input = viewModel.Inputs[1];
        Invoke(view, "OnBrowseFile", new Button { Tag = input }, new RoutedEventArgs(Button.ClickEvent));

        input.Path.Should().Be("/tmp/sample.json");
    }

    [AvaloniaFact]
    public void CopyActiveJson_without_clipboard_keeps_value()
    {
        var service = new FakeDriftbusterService();
        var viewModel = new DiffViewModel(service)
        {
            RawJson = "{\"value\":42}",
        };

        var view = new DiffView
        {
            DataContext = viewModel,
        };

        Invoke(view, "OnCopyActiveJson", new Button(), new RoutedEventArgs(Button.ClickEvent));

        viewModel.RawJson.Should().Be("{\"value\":42}");
    }

    [AvaloniaFact]
    public void CopyActiveJson_with_override_captures_sanitized_payload()
    {
        var captured = new List<string>();
        var viewModel = new DiffViewModel(new FakeDriftbusterService())
        {
            RawJson = "{\"key\":1}",
            SanitizedJson = "{\"safe\":true}",
        };

        viewModel.JsonViewMode = DiffViewModel.DiffJsonViewMode.Sanitized;

        var view = new DiffView
        {
            DataContext = viewModel,
            ClipboardSetTextOverride = text =>
            {
                captured.Add(text);
                return Task.CompletedTask;
            },
        };

        Invoke(view, "OnCopyActiveJson", new Button(), new RoutedEventArgs(Button.ClickEvent));

        captured.Should().ContainSingle().Which.Should().Be("{\"safe\":true}");
    }

    [AvaloniaFact]
    public void CopyActiveJson_refuses_unsanitized_payload_when_sanitized_available()
    {
        var captured = new List<string>();
        var viewModel = new DiffViewModel(new FakeDriftbusterService())
        {
            RawJson = "{\"raw\":true}",
            SanitizedJson = "{\"safe\":true}",
        };

        viewModel.JsonViewMode = DiffViewModel.DiffJsonViewMode.Raw;

        var view = new DiffView
        {
            DataContext = viewModel,
            ClipboardSetTextOverride = text =>
            {
                captured.Add(text);
                return Task.CompletedTask;
            },
        };

        Invoke(view, "OnCopyActiveJson", new Button(), new RoutedEventArgs(Button.ClickEvent));

        captured.Should().BeEmpty();
        viewModel.CanCopyActiveJson.Should().BeFalse();
    }

    private static void Invoke(object target, string methodName, params object?[] args)
    {
        var method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        method.Should().NotBeNull();
        method!.Invoke(target, args);
    }
}