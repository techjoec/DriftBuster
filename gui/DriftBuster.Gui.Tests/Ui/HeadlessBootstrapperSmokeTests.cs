using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Reflection;

using Avalonia;
using Avalonia.Media;
using Avalonia.Platform;

using DriftBuster.Gui.Headless;
using DriftBuster.Gui.Tests.Headless;

using Xunit;

namespace DriftBuster.Gui.Tests.Ui;

[Collection(HeadlessCollection.Name)]
public sealed class HeadlessBootstrapperSmokeTests
{
    [Fact]
    public void EnsureHeadless_registers_inter_font_manager()
    {
        using var telemetry = HeadlessFontHealthTelemetry.BeginScenario(nameof(EnsureHeadless_registers_inter_font_manager));
        try
        {
            using var scope = Program.EnsureHeadless();
            HeadlessFixture.EnsureFonts();

            var locator = typeof(AvaloniaLocator).GetProperty("CurrentMutable", BindingFlags.Public | BindingFlags.Static)
                ?.GetValue(null) as AvaloniaLocator;
            Assert.NotNull(locator);

            var serviceMethod = locator!.GetType().GetMethod("GetService", BindingFlags.Instance | BindingFlags.Public, new[] { typeof(Type) });
            Assert.NotNull(serviceMethod);

            var fontManager = serviceMethod!.Invoke(locator, new object[] { typeof(IFontManagerImpl) });
            var manager = Assert.IsAssignableFrom<IFontManagerImpl>(fontManager);

            var options = serviceMethod.Invoke(locator, new object[] { typeof(FontManagerOptions) });
            var managerOptions = Assert.IsType<FontManagerOptions>(options);

            telemetry.RecordMetric("default_family", managerOptions.DefaultFamilyName);
            var fallbacks = managerOptions.FontFallbacks;
            Assert.NotNull(fallbacks);
            var descriptors = fallbacks!
                .Select(fallback => fallback?.FontFamily)
                .Where(family => family is not null)
                .Cast<FontFamily>()
                .Select(family => new
                {
                    Family = family,
                    Name = family.Name,
                    Descriptor = GetFontFamilyDescriptor(family),
                })
                .ToArray();

            telemetry.RecordMetric("fallback_count", descriptors.Length.ToString(CultureInfo.InvariantCulture));

            var interFallbackPresent = descriptors.Any(entry =>
                string.Equals(entry.Name, "Inter", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(entry.Descriptor, "Inter", StringComparison.OrdinalIgnoreCase));
            Assert.True(interFallbackPresent);
            telemetry.RecordMetric("inter_fallback", interFallbackPresent ? "true" : "false");

            var systemFontsAliasPresent = descriptors.Any(entry =>
                string.Equals(entry.Name, "fonts:SystemFonts", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(entry.Descriptor, "fonts:SystemFonts", StringComparison.OrdinalIgnoreCase));
            Assert.True(systemFontsAliasPresent, "fonts:SystemFonts alias should be part of the fallback chain.");
            telemetry.RecordMetric("system_fonts_alias", systemFontsAliasPresent ? "true" : "false");

            var tryCreateGlyphTypeface = typeof(IFontManagerImpl).GetMethod("TryCreateGlyphTypeface", new[]
            {
                typeof(string), typeof(FontStyle), typeof(FontWeight), typeof(FontStretch), typeof(IGlyphTypeface).MakeByRefType(),
            });

            var parameters = new object?[]
            {
                "fonts:SystemFonts",
                FontStyle.Normal,
                FontWeight.Normal,
                FontStretch.Normal,
                null,
            };

            var success = tryCreateGlyphTypeface is not null && (bool)tryCreateGlyphTypeface.Invoke(manager, parameters)!;
            telemetry.RecordMetric("glyph_alias_success", success ? "true" : "false");
            Assert.True(success);
            var aliasTypeface = Assert.IsAssignableFrom<IGlyphTypeface>(parameters[4]);
            telemetry.RecordMetric("glyph_family", aliasTypeface.FamilyName);

            telemetry.MarkSuccess();
        }
        catch (Exception ex)
        {
            telemetry.MarkFailure(ex);
            throw;
        }
    }

    [Fact]
    public void EnsureHeadless_release_mode_exposes_inter_alias_through_system_fonts()
    {
        using var telemetry = HeadlessFontHealthTelemetry.BeginScenario(nameof(EnsureHeadless_release_mode_exposes_inter_alias_through_system_fonts));
        try
        {
            using var scope = Program.EnsureHeadless();
            HeadlessFixture.EnsureFonts();

            var fontManager = FontManager.Current;
            Assert.NotNull(fontManager);

            var app = Assert.IsType<App>(Application.Current);
            const string resourceKey = "fonts:SystemFonts";
            Assert.True(app.Resources.TryGetValue(resourceKey, out var fontsResource));
            var resourceDictionary = Assert.IsAssignableFrom<IDictionary<string, FontFamily>>(fontsResource);

            var hasSystemFontsAlias = resourceDictionary.ContainsKey(resourceKey);
            var hasInterAlias = resourceDictionary.ContainsKey("Inter");
            telemetry.RecordMetric("resource_contains_system_fonts", hasSystemFontsAlias ? "true" : "false");
            telemetry.RecordMetric("resource_contains_inter", hasInterAlias ? "true" : "false");

            Assert.True(hasSystemFontsAlias);
            Assert.True(hasInterAlias);

            var resolved = fontManager!.TryGetGlyphTypeface(new Typeface("fonts:SystemFonts#Inter"), out var glyphTypeface);
            telemetry.RecordMetric("glyph_resolved", resolved ? "true" : "false");
            Assert.True(resolved, "Expected fonts:SystemFonts#Inter alias to resolve via the font manager in release mode.");
            Assert.NotNull(glyphTypeface);

            var normalised = string.Equals("Inter", glyphTypeface!.FamilyName, StringComparison.OrdinalIgnoreCase);
            telemetry.RecordMetric("glyph_family", glyphTypeface!.FamilyName);
            telemetry.RecordMetric("glyph_family_normalised", normalised ? "true" : "false");
            Assert.True(normalised, $"Expected glyph family to normalise to Inter but was '{glyphTypeface!.FamilyName}'.");

            telemetry.MarkSuccess();
        }
        catch (Exception ex)
        {
            telemetry.MarkFailure(ex);
            throw;
        }
    }

    private static string? GetFontFamilyDescriptor(FontFamily family)
    {
        var sourceProperty = typeof(FontFamily).GetProperty("Source", BindingFlags.Instance | BindingFlags.Public);
        var source = sourceProperty?.GetValue(family) as string;

        return string.IsNullOrWhiteSpace(source)
            ? family.Name
            : source;
    }

    [Fact]
    public void EnsureHeadless_allows_main_window_instantiation()
    {
        using var telemetry = HeadlessFontHealthTelemetry.BeginScenario(nameof(EnsureHeadless_allows_main_window_instantiation));
        try
        {
            using var scope = Program.EnsureHeadless();
            HeadlessFixture.EnsureFonts();

            var window = new DriftBuster.Gui.Views.MainWindow();
            telemetry.RecordMetric("window_type", window.GetType().FullName);
            Assert.NotNull(window);

            telemetry.MarkSuccess();
        }
        catch (Exception ex)
        {
            telemetry.MarkFailure(ex);
            throw;
        }
    }

    [Fact]
    public void EnsureHeadless_reinitialises_existing_application_with_aliases()
    {
        using var initialScope = Program.EnsureHeadless();
        HeadlessFixture.EnsureFonts();

        ResetHeadlessInitialisedFlag();

        using var rerunScope = Program.EnsureHeadless();
        HeadlessFixture.EnsureFonts();

        var fontManager = FontManager.Current;
        Assert.NotNull(fontManager);

        Assert.True(fontManager!.TryGetGlyphTypeface(new Typeface("fonts:SystemFonts"), out var aliasGlyph),
            "Expected fonts:SystemFonts alias to resolve after reinitialising an existing App instance.");
        Assert.NotNull(aliasGlyph);

        Assert.True(fontManager.TryGetGlyphTypeface(new Typeface("fonts:SystemFonts#Inter"), out var interGlyph),
            "Expected fonts:SystemFonts#Inter alias to resolve after reinitialising an existing App instance.");
        Assert.NotNull(interGlyph);
        Assert.Equal("Inter", interGlyph!.FamilyName, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void EnsureHeadless_records_bootstrapper_diagnostics_snapshot()
    {
        using var scope = Program.EnsureHeadless();
        HeadlessFixture.EnsureFonts();

        var snapshot = HeadlessFontBootstrapperDiagnostics.GetSnapshot();

        Assert.True(snapshot.Timestamp > DateTimeOffset.MinValue, "Snapshot timestamp should be recorded.");
        Assert.NotEmpty(snapshot.Probes);
        Assert.True(snapshot.ResourceCount > 0, "Expected font resource dictionary to contain entries.");
        Assert.True(snapshot.ResourceContainsSystemFonts, "fonts:SystemFonts alias should exist in resource dictionary.");
        Assert.True(snapshot.ResourceContainsInter, "Inter alias should exist in resource dictionary.");
        Assert.Contains(snapshot.Probes, probe =>
            string.Equals(probe.Alias, "fonts:SystemFonts", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(snapshot.Probes, probe =>
            string.Equals(probe.Alias, "fonts:SystemFonts#Inter", StringComparison.OrdinalIgnoreCase));
    }

    private static void ResetHeadlessInitialisedFlag()
    {
        var field = typeof(Program).GetField("_headlessInitialized", BindingFlags.Static | BindingFlags.NonPublic);
        field?.SetValue(null, false);
    }
}
