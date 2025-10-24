using System.Collections.Generic;

using Avalonia;

namespace DriftBuster.Gui.Services;

public static class ResponsiveSpacingProfiles
{
    public static IReadOnlyList<ResponsiveBreakpoint> MainWindow { get; } = new[]
    {
        new ResponsiveBreakpoint(
            MinWidth: 0,
            Resources: new Dictionary<string, object>
            {
                ["Layout.HeaderPadding"] = new Thickness(20, 16, 20, 16),
                ["Layout.ContentMargin"] = new Thickness(20),
                ["Layout.SectionCardPadding"] = new Thickness(16),
                ["Layout.PrimaryCardPadding"] = new Thickness(24),
                ["Layout.SectionSpacing"] = new Thickness(0, 16, 0, 0),
                ["Toast.Width"] = 320d,
                ["Toast.Padding"] = new Thickness(14),
                ["Toast.CardMargin"] = new Thickness(0, 0, 0, 8),
                ["Toast.IconFontSize"] = 20d,
                ["Toast.InnerSpacing"] = 8d,
                ["Toast.HostMargin"] = new Thickness(0, 24, 24, 0),
                ["Toast.StackSpacing"] = 8d,
            }),
        new ResponsiveBreakpoint(
            MinWidth: 1280,
            Resources: new Dictionary<string, object>
            {
                ["Layout.HeaderPadding"] = new Thickness(24, 20, 24, 20),
                ["Layout.ContentMargin"] = new Thickness(24),
                ["Layout.SectionCardPadding"] = new Thickness(18),
                ["Layout.PrimaryCardPadding"] = new Thickness(28),
                ["Layout.SectionSpacing"] = new Thickness(0, 20, 0, 0),
                ["Toast.Width"] = 360d,
                ["Toast.Padding"] = new Thickness(16),
                ["Toast.CardMargin"] = new Thickness(0, 0, 0, 10),
                ["Toast.IconFontSize"] = 22d,
                ["Toast.InnerSpacing"] = 10d,
                ["Toast.HostMargin"] = new Thickness(0, 28, 28, 0),
                ["Toast.StackSpacing"] = 10d,
            }),
        new ResponsiveBreakpoint(
            MinWidth: 1600,
            Resources: new Dictionary<string, object>
            {
                ["Layout.HeaderPadding"] = new Thickness(28, 22, 28, 22),
                ["Layout.ContentMargin"] = new Thickness(28),
                ["Layout.SectionCardPadding"] = new Thickness(20),
                ["Layout.PrimaryCardPadding"] = new Thickness(32),
                ["Layout.SectionSpacing"] = new Thickness(0, 24, 0, 0),
                ["Toast.Width"] = 400d,
                ["Toast.Padding"] = new Thickness(18),
                ["Toast.CardMargin"] = new Thickness(0, 0, 0, 12),
                ["Toast.IconFontSize"] = 24d,
                ["Toast.InnerSpacing"] = 12d,
                ["Toast.HostMargin"] = new Thickness(0, 32, 32, 0),
                ["Toast.StackSpacing"] = 12d,
            }),
        new ResponsiveBreakpoint(
            MinWidth: 1920,
            Resources: new Dictionary<string, object>
            {
                ["Layout.HeaderPadding"] = new Thickness(32, 24, 32, 24),
                ["Layout.ContentMargin"] = new Thickness(32),
                ["Layout.SectionCardPadding"] = new Thickness(22),
                ["Layout.PrimaryCardPadding"] = new Thickness(36),
                ["Layout.SectionSpacing"] = new Thickness(0, 28, 0, 0),
                ["Toast.Width"] = 440d,
                ["Toast.Padding"] = new Thickness(20),
                ["Toast.CardMargin"] = new Thickness(0, 0, 0, 16),
                ["Toast.IconFontSize"] = 24d,
                ["Toast.InnerSpacing"] = 12d,
                ["Toast.HostMargin"] = new Thickness(0, 36, 36, 0),
                ["Toast.StackSpacing"] = 12d,
            }),
    };

    public static IReadOnlyList<ResponsiveBreakpoint> ServerSelection { get; } = new[]
    {
        new ResponsiveBreakpoint(
            MinWidth: 0,
            Resources: new Dictionary<string, object>
            {
                ["ServerSelection.OuterMargin"] = new Thickness(0, 0, 0, 24),
                ["ServerSelection.StackSpacing"] = 18d,
                ["ServerSelection.HeaderColumnSpacing"] = 12d,
                ["ServerSelection.SectionSpacing"] = 16d,
                ["ServerSelection.CardPadding"] = new Thickness(16),
                ["ServerSelection.CardMargin"] = new Thickness(0, 0, 0, 16),
                ["ServerSelection.HighlightPadding"] = new Thickness(18),
            }),
        new ResponsiveBreakpoint(
            MinWidth: 1280,
            Resources: new Dictionary<string, object>
            {
                ["ServerSelection.OuterMargin"] = new Thickness(0, 0, 0, 28),
                ["ServerSelection.StackSpacing"] = 20d,
                ["ServerSelection.HeaderColumnSpacing"] = 14d,
                ["ServerSelection.SectionSpacing"] = 18d,
                ["ServerSelection.CardPadding"] = new Thickness(18),
                ["ServerSelection.CardMargin"] = new Thickness(0, 0, 0, 20),
                ["ServerSelection.HighlightPadding"] = new Thickness(20),
            }),
        new ResponsiveBreakpoint(
            MinWidth: 1600,
            Resources: new Dictionary<string, object>
            {
                ["ServerSelection.OuterMargin"] = new Thickness(0, 0, 0, 32),
                ["ServerSelection.StackSpacing"] = 22d,
                ["ServerSelection.HeaderColumnSpacing"] = 16d,
                ["ServerSelection.SectionSpacing"] = 20d,
                ["ServerSelection.CardPadding"] = new Thickness(20),
                ["ServerSelection.CardMargin"] = new Thickness(0, 0, 0, 24),
                ["ServerSelection.HighlightPadding"] = new Thickness(22),
            }),
        new ResponsiveBreakpoint(
            MinWidth: 1920,
            Resources: new Dictionary<string, object>
            {
                ["ServerSelection.OuterMargin"] = new Thickness(0, 0, 0, 36),
                ["ServerSelection.StackSpacing"] = 24d,
                ["ServerSelection.HeaderColumnSpacing"] = 18d,
                ["ServerSelection.SectionSpacing"] = 22d,
                ["ServerSelection.CardPadding"] = new Thickness(22),
                ["ServerSelection.CardMargin"] = new Thickness(0, 0, 0, 28),
                ["ServerSelection.HighlightPadding"] = new Thickness(24),
            }),
    };
}
