using System;
using System.Collections.Generic;
using System.Linq;

using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace DriftBuster.Gui.Services;

public sealed record ResponsiveBreakpoint(double MinWidth, IReadOnlyDictionary<string, object> Resources);

public static class ResponsiveLayoutService
{
    public static IDisposable Attach(Control control, IReadOnlyList<ResponsiveBreakpoint> breakpoints)
    {
        if (control is null)
        {
            throw new ArgumentNullException(nameof(control));
        }

        if (breakpoints is null || breakpoints.Count == 0)
        {
            throw new ArgumentException("At least one breakpoint is required.", nameof(breakpoints));
        }

        var orderedBreakpoints = breakpoints
            .OrderBy(static breakpoint => breakpoint.MinWidth)
            .ToArray();

        ApplyInternal(control, GetInitialWidth(control), orderedBreakpoints);

        EventHandler<AvaloniaPropertyChangedEventArgs>? topLevelHandler = null;
        TopLevel? currentTopLevel = null;

        void ControlPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == Visual.BoundsProperty && e.NewValue is Rect rect)
            {
                ApplyInternal(control, rect.Width, orderedBreakpoints);
            }
        }

        control.PropertyChanged += ControlPropertyChanged;

        void AttachTopLevel()
        {
            if (currentTopLevel is not null && topLevelHandler is not null)
            {
                currentTopLevel.PropertyChanged -= topLevelHandler;
                topLevelHandler = null;
                currentTopLevel = null;
            }

            var topLevel = TopLevel.GetTopLevel(control);
            if (topLevel is null)
            {
                return;
            }

            currentTopLevel = topLevel;
            topLevelHandler = (sender, args) =>
            {
                if (args.Property == TopLevel.ClientSizeProperty && args.NewValue is Size size)
                {
                    ApplyInternal(control, size.Width, orderedBreakpoints);
                }
            };

            topLevel.PropertyChanged += topLevelHandler;
        }

        if (control.IsAttachedToVisualTree())
        {
            AttachTopLevel();
        }

        void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
        {
            AttachTopLevel();
            ApplyInternal(control, GetInitialWidth(control), orderedBreakpoints);
        }

        void OnDetached(object? sender, VisualTreeAttachmentEventArgs e)
        {
            if (currentTopLevel is not null && topLevelHandler is not null)
            {
                currentTopLevel.PropertyChanged -= topLevelHandler;
                topLevelHandler = null;
                currentTopLevel = null;
            }
        }

        control.AttachedToVisualTree += OnAttached;
        control.DetachedFromVisualTree += OnDetached;

        return new DelegateDisposable(() =>
        {
            control.PropertyChanged -= ControlPropertyChanged;
            if (currentTopLevel is not null && topLevelHandler is not null)
            {
                currentTopLevel.PropertyChanged -= topLevelHandler;
            }
            control.AttachedToVisualTree -= OnAttached;
            control.DetachedFromVisualTree -= OnDetached;
        });
    }

    public static void Apply(Control control, double width, IReadOnlyList<ResponsiveBreakpoint> breakpoints)
    {
        if (control is null)
        {
            throw new ArgumentNullException(nameof(control));
        }

        if (breakpoints is null || breakpoints.Count == 0)
        {
            throw new ArgumentException("At least one breakpoint is required.", nameof(breakpoints));
        }

        var orderedBreakpoints = breakpoints
            .OrderBy(static breakpoint => breakpoint.MinWidth)
            .ToArray();

        ApplyInternal(control, width, orderedBreakpoints);
    }

    private static void ApplyInternal(Control control, double width, IReadOnlyList<ResponsiveBreakpoint> orderedBreakpoints)
    {
        var activeBreakpoint = orderedBreakpoints[0];

        foreach (var breakpoint in orderedBreakpoints)
        {
            if (width < breakpoint.MinWidth)
            {
                break;
            }

            activeBreakpoint = breakpoint;
        }

        foreach (var resource in activeBreakpoint.Resources)
        {
            control.Resources[resource.Key] = resource.Value;
        }
    }

    private static double GetInitialWidth(Control control)
    {
        var topLevel = TopLevel.GetTopLevel(control);
        if (topLevel is not null)
        {
            return topLevel.ClientSize.Width;
        }

        return control.Bounds.Width;
    }

    private sealed class DelegateDisposable : IDisposable
    {
        private readonly Action _onDispose;
        private bool _disposed;

        public DelegateDisposable(Action onDispose)
        {
            _onDispose = onDispose ?? throw new ArgumentNullException(nameof(onDispose));
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _onDispose();
        }
    }
}
