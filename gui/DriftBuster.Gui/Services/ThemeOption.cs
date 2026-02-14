using Avalonia.Styling;

namespace DriftBuster.Gui.Services;

public sealed record ThemeOption(string Id, string DisplayName, ThemeVariant Variant, string PaletteResourceKey);
