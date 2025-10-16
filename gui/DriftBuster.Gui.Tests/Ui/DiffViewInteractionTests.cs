using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;

using Avalonia.Controls;
using Avalonia.Interactivity;

using DriftBuster.Gui.Tests.Fakes;
using DriftBuster.Gui.ViewModels;
using DriftBuster.Gui.Views;

namespace DriftBuster.Gui.Tests.Ui;

[Collection(HeadlessCollection.Name)]
public sealed class DiffViewInteractionTests
{
    [Fact]
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

    [Fact]
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

    [Fact]
    public void CopyRawJson_without_clipboard_keeps_value()
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

        Invoke(view, "OnCopyRawJson", new Button(), new RoutedEventArgs(Button.ClickEvent));

        viewModel.RawJson.Should().Be("{\"value\":42}");
    }

    [Fact]
    public void CopyRawJson_with_override_captures_text()
    {
        var captured = new List<string>();
        var viewModel = new DiffViewModel(new FakeDriftbusterService())
        {
            RawJson = "{\"key\":1}",
        };

        var view = new DiffView
        {
            DataContext = viewModel,
            ClipboardSetTextOverride = text =>
            {
                captured.Add(text);
                return Task.CompletedTask;
            },
        };

        Invoke(view, "OnCopyRawJson", new Button(), new RoutedEventArgs(Button.ClickEvent));

        captured.Should().ContainSingle().Which.Should().Be("{\"key\":1}");
    }

    private static void Invoke(object target, string methodName, params object?[] args)
    {
        var method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        method.Should().NotBeNull();
        method!.Invoke(target, args);
    }
}
