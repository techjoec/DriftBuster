using System.Threading.Tasks;
using Avalonia.Headless.XUnit;

using Avalonia.Controls;
using Avalonia.Interactivity;

using DriftBuster.Gui.Tests.Fakes;
using DriftBuster.Gui.ViewModels;
using DriftBuster.Gui.Views;

namespace DriftBuster.Gui.Tests.Ui;

[Collection(HeadlessCollection.Name)]
public sealed class HuntViewInteractionTests
{
    [AvaloniaFact]
    public void BrowseDirectory_without_provider_keeps_value()
    {
        var viewModel = new HuntViewModel(new FakeDriftbusterService());
        var view = new HuntView
        {
            DataContext = viewModel,
        };

        viewModel.DirectoryPath = "existing";
        Invoke(view, "OnBrowseDirectory", new Button(), new RoutedEventArgs(Button.ClickEvent));

        viewModel.DirectoryPath.Should().Be("existing");
    }

    [AvaloniaFact]
    public void BrowseDirectory_with_overrides_sets_directory()
    {
        var viewModel = new HuntViewModel(new FakeDriftbusterService());
        var view = new HuntView
        {
            DataContext = viewModel,
            FolderPickerOverride = () => Task.FromResult<string?>("/tmp/folder"),
            FilePickerOverride = () => Task.FromResult<string?>("/tmp/file.txt"),
        };

        Invoke(view, "OnBrowseDirectory", new Button(), new RoutedEventArgs(Button.ClickEvent));

        viewModel.DirectoryPath.Should().Be("/tmp/folder");
    }

    private static void Invoke(object target, string methodName, params object?[] args)
    {
        var method = target.GetType().GetMethod(methodName, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        method.Should().NotBeNull();
        method!.Invoke(target, args);
    }
}