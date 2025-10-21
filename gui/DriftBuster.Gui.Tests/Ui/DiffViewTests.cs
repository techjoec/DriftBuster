using Avalonia.Headless.XUnit;
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
}
