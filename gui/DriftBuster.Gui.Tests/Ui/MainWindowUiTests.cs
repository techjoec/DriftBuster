using Avalonia.Headless.XUnit;
using DriftBuster.Gui.ViewModels;
using DriftBuster.Gui.Views;

using FluentAssertions;
using Xunit;

namespace DriftBuster.Gui.Tests.Ui;

[Collection(HeadlessCollection.Name)]
public sealed class MainWindowUiTests
{
    [AvaloniaFact]
    public void Should_Create_MainWindow_With_ViewModel()
    {
        var window = new MainWindow
        {
            DataContext = new MainWindowViewModel(),
        };

        window.DataContext.Should().BeOfType<MainWindowViewModel>();
    }
}
