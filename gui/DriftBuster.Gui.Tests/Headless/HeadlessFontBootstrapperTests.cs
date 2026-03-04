using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using Avalonia.Platform;
using DriftBuster.Gui.Headless;
using DriftBuster.Gui.Tests.Ui;

namespace DriftBuster.Gui.Tests.Headless;

[Collection(HeadlessCollection.Name)]
public sealed class HeadlessFontBootstrapperTests
{
    [AvaloniaFact]
    public void Ensure_is_idempotent_and_records_usable_snapshot()
    {
        HeadlessFixture.EnsureFonts();

        HeadlessFontBootstrapper.Ensure();
        var first = HeadlessFontBootstrapperDiagnostics.GetSnapshot();

        HeadlessFontBootstrapper.Ensure();
        var second = HeadlessFontBootstrapperDiagnostics.GetSnapshot();

        second.Timestamp.Should().BeOnOrAfter(first.Timestamp);
        second.Probes.Should().NotBeEmpty();
        second.Probes.Should().Contain(probe =>
            string.Equals(probe.Alias, "fonts:SystemFonts", StringComparison.OrdinalIgnoreCase));
    }

    [AvaloniaFact]
    public void Ensure_handles_missing_fonts_resource_dictionary_without_regression()
    {
        HeadlessFixture.EnsureFonts();
        var app = Application.Current.Should().BeOfType<Application>().Subject;
        const string key = "fonts:SystemFonts";
        var hadExisting = app.Resources.TryGetValue(key, out var previous);

        try
        {
            app.Resources.Remove(key);
            HeadlessFontBootstrapper.Ensure();
            HeadlessFontBootstrapper.RepairDesktopFontResolution();
            FontManager.Current.TryGetGlyphTypeface(new Typeface("fonts:SystemFonts"), out _).Should().BeTrue();
        }
        finally
        {
            if (hadExisting)
            {
                app.Resources[key] = previous!;
            }
            else
            {
                app.Resources.Remove(key);
            }
        }
    }

    [AvaloniaFact]
    public void Ensure_and_repair_keep_core_font_aliases_resolvable()
    {
        HeadlessFixture.EnsureFonts();

        HeadlessFontBootstrapper.Ensure();
        HeadlessFontBootstrapper.RepairDesktopFontResolution();

        FontManager.Current.TryGetGlyphTypeface(new Typeface("Inter"), out var inter).Should().BeTrue();
        inter.Should().NotBeNull();
        FontManager.Current.TryGetGlyphTypeface(new Typeface("fonts:SystemFonts"), out var system).Should().BeTrue();
        system.Should().NotBeNull();
        FontManager.Current.TryGetGlyphTypeface(new Typeface("fonts:SystemFonts#Inter"), out var interAlias).Should().BeTrue();
        interAlias.Should().NotBeNull();
    }

    [AvaloniaFact]
    public void RepairDesktopFontResolution_executes_and_preserves_system_alias()
    {
        HeadlessFixture.EnsureFonts();

        HeadlessFontBootstrapper.RepairDesktopFontResolution();

        FontManager.Current.TryGetGlyphTypeface(new Typeface("fonts:SystemFonts"), out var glyphTypeface)
            .Should().BeTrue();
        glyphTypeface.Should().NotBeNull();
    }

    [AvaloniaFact]
    public void TryRefreshSystemFontCollection_wraps_existing_non_alias_collection()
    {
        HeadlessFixture.EnsureFonts();
        var fontManager = FontManager.Current;

        var field = typeof(FontManager).GetField("_fontCollections", BindingFlags.Instance | BindingFlags.NonPublic);
        field.Should().NotBeNull();
        var original = field!.GetValue(fontManager);

        var systemFontsUriField = typeof(HeadlessFontBootstrapper).GetField("SystemFontsUri", BindingFlags.Static | BindingFlags.NonPublic);
        systemFontsUriField.Should().NotBeNull();
        var key = systemFontsUriField!.GetValue(null).Should().BeOfType<Uri>().Subject;

        var collections = new ConcurrentDictionary<Uri, IFontCollection>()
        {
            [key] = new StubFontCollection(),
        };
        field.SetValue(fontManager, collections);

        var method = typeof(HeadlessFontBootstrapper).GetMethod(
            "TryRefreshSystemFontCollection",
            BindingFlags.NonPublic | BindingFlags.Static,
            binder: null,
            types: new[] { typeof(FontManager), typeof(ICollection<string>) },
            modifiers: null);
        method.Should().NotBeNull();
        var failures = new List<string>();

        try
        {
            method.Invoke(null, new object?[] { fontManager, failures });
            collections[key].Should().BeOfType<HeadlessAliasFontCollection>();
            failures.Should().Contain(note => note.StartsWith("wrapped_collection:", StringComparison.Ordinal));

            var existingAlias = collections[key];
            method.Invoke(null, new object?[] { fontManager, failures });
            collections[key].Should().BeSameAs(existingAlias);
        }
        finally
        {
            field.SetValue(fontManager, original);
        }
    }

    private sealed class StubFontCollection : IFontCollection
    {
        public Uri Key { get; } = new("fonts:stub-system");

        public int Count => 1;

        public FontFamily this[int index] => new("Inter");

        public void Initialize(IFontManagerImpl fontManager)
        {
        }

        public bool TryGetGlyphTypeface(
            string familyName,
            FontStyle style,
            FontWeight weight,
            FontStretch stretch,
            [NotNullWhen(true)] out IGlyphTypeface? glyphTypeface)
        {
            glyphTypeface = null;
            return false;
        }

        public bool TryMatchCharacter(
            int codepoint,
            FontStyle fontStyle,
            FontWeight fontWeight,
            FontStretch fontStretch,
            string? familyName,
            CultureInfo? culture,
            out Typeface typeface)
        {
            typeface = default;
            return false;
        }

        public IEnumerator<FontFamily> GetEnumerator()
        {
            yield return new FontFamily("Inter");
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public void Dispose()
        {
        }
    }
}
