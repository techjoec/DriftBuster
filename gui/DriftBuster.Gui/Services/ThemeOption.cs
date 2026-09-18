using Avalonia.Styling;

namespace DriftBuster.Gui.Services;

public sealed record ThemeOption(string Id, string DisplayName, ThemeVariant Variant, string PaletteResourceKey)
{
    // UI Automation reads a combo box's selected value through ToString.
    public override string ToString() => DisplayName;
}
