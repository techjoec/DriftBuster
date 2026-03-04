using System;
using System.Collections.Generic;

using Avalonia.Headless.XUnit;
using Avalonia.Controls;

using DriftBuster.Gui.Services;
using DriftBuster.Gui.Tests.Ui;

namespace DriftBuster.Gui.Tests.Services;

[Collection(HeadlessCollection.Name)]
public sealed class ResponsiveLayoutServiceTests
{
    [AvaloniaFact]
    public void Apply_requires_control_and_breakpoints()
    {
        var control = new Border();

        Action nullControl = () => ResponsiveLayoutService.Apply(null!, 1200, new List<ResponsiveBreakpoint>
        {
            new(0, new Dictionary<string, object>(StringComparer.Ordinal)),
        });

        Action emptyBreakpoints = () => ResponsiveLayoutService.Apply(control, 1200, Array.Empty<ResponsiveBreakpoint>());

        nullControl.Should().Throw<ArgumentNullException>();
        emptyBreakpoints.Should().Throw<ArgumentException>();
    }

    [AvaloniaFact]
    public void Attach_requires_control_and_breakpoints()
    {
        var control = new Border();

        Action nullControl = () => ResponsiveLayoutService.Attach(null!, new List<ResponsiveBreakpoint>
        {
            new(0, new Dictionary<string, object>(StringComparer.Ordinal)),
        });

        Action emptyBreakpoints = () => ResponsiveLayoutService.Attach(control, Array.Empty<ResponsiveBreakpoint>());

        nullControl.Should().Throw<ArgumentNullException>();
        emptyBreakpoints.Should().Throw<ArgumentException>();
    }

    [AvaloniaFact]
    public void Apply_selects_last_matching_breakpoint()
    {
        var control = new Border();
        var breakpoints = new[]
        {
            new ResponsiveBreakpoint(0, new Dictionary<string, object>(StringComparer.Ordinal) { ["Layout.Padding"] = 8d }),
            new ResponsiveBreakpoint(1000, new Dictionary<string, object>(StringComparer.Ordinal) { ["Layout.Padding"] = 12d }),
            new ResponsiveBreakpoint(1600, new Dictionary<string, object>(StringComparer.Ordinal) { ["Layout.Padding"] = 20d }),
        };

        ResponsiveLayoutService.Apply(control, 999, breakpoints);
        control.Resources["Layout.Padding"].Should().Be(8d);

        ResponsiveLayoutService.Apply(control, 1000, breakpoints);
        control.Resources["Layout.Padding"].Should().Be(12d);

        ResponsiveLayoutService.Apply(control, 2200, breakpoints);
        control.Resources["Layout.Padding"].Should().Be(20d);
    }

    [AvaloniaFact]
    public void Attach_returns_disposable_that_is_safe_to_dispose_multiple_times()
    {
        var control = new Border();
        var breakpoints = new[]
        {
            new ResponsiveBreakpoint(0, new Dictionary<string, object>(StringComparer.Ordinal) { ["Layout.Padding"] = 8d }),
        };

        var handle = ResponsiveLayoutService.Attach(control, breakpoints);

        control.Resources["Layout.Padding"].Should().Be(8d);

        Action disposeTwice = () =>
        {
            handle.Dispose();
            handle.Dispose();
        };

        disposeTwice.Should().NotThrow();
    }
}
