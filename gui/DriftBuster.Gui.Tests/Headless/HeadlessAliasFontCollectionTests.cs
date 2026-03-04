using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using Avalonia.Platform;
using DriftBuster.Gui.Headless;

namespace DriftBuster.Gui.Tests.Headless;

public sealed class HeadlessAliasFontCollectionTests
{
    [Fact]
    public void Constructor_throws_when_inner_collection_is_null()
    {
        var action = () => new HeadlessAliasFontCollection(null!, "Inter");
        action.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Initialize_merges_inner_families_with_aliases()
    {
        var inner = new StubFontCollection
        {
            Families = new[] { new FontFamily("Inter"), new FontFamily("Custom") },
        };

        var collection = new HeadlessAliasFontCollection(inner, "Inter");
        collection.Initialize(CreateFontManagerStub());

        inner.InitializeCalls.Should().Be(1);
        collection.Count.Should().BeGreaterThanOrEqualTo(3);
        collection.Select(family => family.Name).Should().Contain(new[]
        {
            "Inter",
            "Custom",
            "fonts:SystemFonts",
        });
    }

    [Fact]
    public void TryGetGlyphTypeface_normalises_aliases_and_falls_back_to_default()
    {
        var requestedFamilies = new List<string>();
        var glyph = CreateGlyphTypefaceStub();
        var inner = new StubFontCollection
        {
            Families = new[] { new FontFamily("Inter") },
            TryGetGlyphTypefaceHandler = (string family, FontStyle _style, FontWeight _weight, FontStretch _stretch, out IGlyphTypeface? resolved) =>
            {
                requestedFamilies.Add(family);
                if (string.Equals(family, "Inter", StringComparison.OrdinalIgnoreCase))
                {
                    resolved = glyph;
                    return true;
                }

                resolved = null;
                return false;
            },
        };

        var collection = new HeadlessAliasFontCollection(inner, "Inter");
        collection.Initialize(CreateFontManagerStub());

        collection.TryGetGlyphTypeface("fonts:SystemFonts#Inter", FontStyle.Normal, FontWeight.Normal, FontStretch.Normal, out var aliasGlyph)
            .Should().BeTrue();
        aliasGlyph.Should().BeSameAs(glyph);

        collection.TryGetGlyphTypeface("MissingFamily", FontStyle.Normal, FontWeight.Normal, FontStretch.Normal, out var fallbackGlyph)
            .Should().BeTrue();
        fallbackGlyph.Should().BeSameAs(glyph);
        requestedFamilies.Should().ContainInOrder("Inter", "MissingFamily", "Inter");
    }

    [Fact]
    public void TryMatchCharacter_normalises_alias_results_and_returns_default_on_failure()
    {
        var inner = new StubFontCollection
        {
            Families = new[] { new FontFamily("Inter") },
            TryMatchCharacterHandler = (
                int _codepoint,
                FontStyle style,
                FontWeight weight,
                FontStretch stretch,
                string? family,
                CultureInfo? _culture,
                out Typeface resolved) =>
            {
                if (string.Equals(family, "Inter", StringComparison.OrdinalIgnoreCase))
                {
                    resolved = new Typeface("fonts:SystemFonts", style, weight, stretch);
                    return true;
                }

                resolved = default;
                return false;
            },
        };

        var collection = new HeadlessAliasFontCollection(inner, "Inter");
        collection.Initialize(CreateFontManagerStub());

        collection.TryMatchCharacter(0x41, FontStyle.Italic, FontWeight.Bold, FontStretch.Expanded, "fonts:SystemFonts", CultureInfo.InvariantCulture, out var matched)
            .Should().BeTrue();
        matched.FontFamily.Name.Should().Be("Inter");

        inner.TryMatchCharacterHandler = (
            int _codepoint,
            FontStyle _style,
            FontWeight _weight,
            FontStretch _stretch,
            string? _family,
            CultureInfo? _culture,
            out Typeface resolved) =>
        {
            resolved = default;
            return false;
        };

        collection.TryMatchCharacter(0x42, FontStyle.Normal, FontWeight.Normal, FontStretch.Normal, "Missing", CultureInfo.InvariantCulture, out var fallback)
            .Should().BeFalse();
        fallback.FontFamily.Name.Should().Be("Inter");
    }

    [Fact]
    public void Dispose_delegates_to_inner_collection()
    {
        var inner = new StubFontCollection
        {
            Families = new[] { new FontFamily("Inter") },
        };
        var collection = new HeadlessAliasFontCollection(inner, "Inter");
        collection.Initialize(CreateFontManagerStub());

        collection.Dispose();

        inner.DisposeCalls.Should().Be(1);
    }

    [Fact]
    public void Exposes_key_indexer_and_non_generic_enumerator()
    {
        var inner = new StubFontCollection
        {
            Families = new[] { new FontFamily("Inter"), new FontFamily("Custom") },
        };

        var collection = new HeadlessAliasFontCollection(inner, "Inter");
        collection.Initialize(CreateFontManagerStub());

        collection.Key.Should().Be(inner.Key);
        collection[0].Should().NotBeNull();
        ((IEnumerable)collection).Cast<FontFamily>().Should().NotBeEmpty();
    }

    [Fact]
    public void TryGetGlyphTypeface_returns_false_when_default_family_missing()
    {
        static bool AlwaysMissing(string familyName, FontStyle style, FontWeight weight, FontStretch stretch, out IGlyphTypeface? glyph)
        {
            glyph = null;
            return false;
        }

        var inner = new StubFontCollection
        {
            Families = new[] { new FontFamily("Inter") },
            TryGetGlyphTypefaceHandler = AlwaysMissing,
        };

        var collection = new HeadlessAliasFontCollection(inner, "Inter");
        collection.Initialize(CreateFontManagerStub());

        collection.TryGetGlyphTypeface("Inter", FontStyle.Normal, FontWeight.Normal, FontStretch.Normal, out var glyphTypeface)
            .Should().BeFalse();
        glyphTypeface.Should().BeNull();
    }

    [Fact]
    public void TryMatchCharacter_uses_secondary_default_lookup_before_failing()
    {
        var observedFamilies = new List<string?>();

        bool TryMatchWithDefaultFallback(
            int codepoint,
            FontStyle style,
            FontWeight weight,
            FontStretch stretch,
            string? family,
            CultureInfo? culture,
            out Typeface resolved)
        {
            observedFamilies.Add(family);
            if (string.Equals(family, "Inter", StringComparison.OrdinalIgnoreCase))
            {
                resolved = new Typeface("Inter", style, weight, stretch);
                return true;
            }

            resolved = default;
            return false;
        }

        var inner = new StubFontCollection
        {
            Families = new[] { new FontFamily("Inter") },
            TryMatchCharacterHandler = TryMatchWithDefaultFallback,
        };

        var collection = new HeadlessAliasFontCollection(inner, "Inter");
        collection.Initialize(CreateFontManagerStub());

        collection.TryMatchCharacter(0x61, FontStyle.Normal, FontWeight.Normal, FontStretch.Normal, "Missing", CultureInfo.InvariantCulture, out var typeface)
            .Should().BeTrue();
        observedFamilies.Should().Equal(new[] { "Missing", "Inter" });
        typeface.FontFamily.Name.Should().Be("Inter");
    }

    private static IGlyphTypeface CreateGlyphTypefaceStub()
        => DispatchProxy.Create<IGlyphTypeface, GlyphTypefaceStub>()!;

    private static IFontManagerImpl CreateFontManagerStub()
        => DispatchProxy.Create<IFontManagerImpl, FontManagerImplStub>()!;

    private sealed class StubFontCollection : IFontCollection
    {
        public delegate bool TryGetGlyphTypefaceDelegate(
            string familyName,
            FontStyle style,
            FontWeight weight,
            FontStretch stretch,
            out IGlyphTypeface? glyphTypeface);

        public delegate bool TryMatchCharacterDelegate(
            int codepoint,
            FontStyle fontStyle,
            FontWeight fontWeight,
            FontStretch fontStretch,
            string? familyName,
            CultureInfo? culture,
            out Typeface typeface);

        public IReadOnlyList<FontFamily> Families { get; init; } = Array.Empty<FontFamily>();

        public TryGetGlyphTypefaceDelegate? TryGetGlyphTypefaceHandler { get; init; }

        public TryMatchCharacterDelegate? TryMatchCharacterHandler { get; set; }

        public int InitializeCalls { get; private set; }

        public int DisposeCalls { get; private set; }

        public Uri Key { get; } = new("fonts:StubCollection");

        public int Count => Families.Count;

        public FontFamily this[int index] => Families[index];

        public void Initialize(IFontManagerImpl fontManager)
        {
            InitializeCalls++;
        }

        public bool TryGetGlyphTypeface(
            string familyName,
            FontStyle style,
            FontWeight weight,
            FontStretch stretch,
            [NotNullWhen(true)] out IGlyphTypeface? glyphTypeface)
        {
            if (TryGetGlyphTypefaceHandler is null)
            {
                glyphTypeface = null;
                return false;
            }

            return TryGetGlyphTypefaceHandler(familyName, style, weight, stretch, out glyphTypeface);
        }

        public bool TryMatchCharacter(int codepoint, FontStyle fontStyle, FontWeight fontWeight, FontStretch fontStretch, string? familyName, CultureInfo? culture, out Typeface typeface)
        {
            if (TryMatchCharacterHandler is null)
            {
                typeface = new Typeface("Inter", fontStyle, fontWeight, fontStretch);
                return false;
            }

            return TryMatchCharacterHandler(codepoint, fontStyle, fontWeight, fontStretch, familyName, culture, out typeface);
        }

        public IEnumerator<FontFamily> GetEnumerator() => Families.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public void Dispose()
        {
            DisposeCalls++;
        }
    }

    private class GlyphTypefaceStub : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod is null)
            {
                return null;
            }

            return targetMethod.ReturnType.IsValueType
                ? Activator.CreateInstance(targetMethod.ReturnType)
                : null;
        }
    }

    private class FontManagerImplStub : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod is null)
            {
                return null;
            }

            return targetMethod.ReturnType.IsValueType
                ? Activator.CreateInstance(targetMethod.ReturnType)
                : null;
        }
    }
}
