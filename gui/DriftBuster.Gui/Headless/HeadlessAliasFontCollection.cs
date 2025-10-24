using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;

using Avalonia.Media;
using Avalonia.Media.Fonts;
using Avalonia.Platform;

namespace DriftBuster.Gui.Headless;

internal sealed class HeadlessAliasFontCollection : IFontCollection
{
    private readonly IFontCollection _inner;
    private readonly string _defaultFamily;
    private readonly FontFamily[] _aliases;
    private IReadOnlyList<FontFamily> _families;

    public HeadlessAliasFontCollection(IFontCollection inner, string defaultFamily)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _defaultFamily = string.IsNullOrWhiteSpace(defaultFamily) ? "Inter" : defaultFamily;
        _aliases = new[]
        {
            new FontFamily(_defaultFamily),
            new FontFamily("fonts:SystemFonts"),
            new FontFamily($"fonts:SystemFonts#{_defaultFamily}"),
        };
        _families = Array.Empty<FontFamily>();
    }

    public Uri Key => _inner.Key;

    public int Count => _families.Count;

    public FontFamily this[int index] => _families[index];

    public void Initialize(IFontManagerImpl fontManager)
    {
        _inner.Initialize(fontManager);
        _families = BuildFamilies();
    }

    public bool TryGetGlyphTypeface(string familyName, FontStyle style, FontWeight weight, FontStretch stretch, [NotNullWhen(true)] out IGlyphTypeface? glyphTypeface)
    {
        var normalised = NormalizeFamilyName(familyName);
        if (_inner.TryGetGlyphTypeface(normalised, style, weight, stretch, out glyphTypeface))
        {
            return true;
        }

        if (!string.Equals(normalised, _defaultFamily, StringComparison.OrdinalIgnoreCase))
        {
            return _inner.TryGetGlyphTypeface(_defaultFamily, style, weight, stretch, out glyphTypeface);
        }

        return false;
    }

    public bool TryMatchCharacter(int codepoint, FontStyle fontStyle, FontWeight fontWeight, FontStretch fontStretch, string? familyName, CultureInfo? culture, out Typeface typeface)
    {
        var normalised = NormalizeFamilyName(familyName);
        if (_inner.TryMatchCharacter(codepoint, fontStyle, fontWeight, fontStretch, normalised, culture, out typeface))
        {
            typeface = NormaliseTypeface(typeface);
            return true;
        }

        if (!string.Equals(normalised, _defaultFamily, StringComparison.OrdinalIgnoreCase) &&
            _inner.TryMatchCharacter(codepoint, fontStyle, fontWeight, fontStretch, _defaultFamily, culture, out typeface))
        {
            typeface = NormaliseTypeface(typeface);
            return true;
        }

        typeface = new Typeface(_defaultFamily, fontStyle, fontWeight, fontStretch);
        return false;
    }

    public IEnumerator<FontFamily> GetEnumerator() => _families.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public void Dispose() => _inner.Dispose();

    private IReadOnlyList<FontFamily> BuildFamilies()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var families = new List<FontFamily>();

        foreach (var family in _inner)
        {
            if (seen.Add(family.Name))
            {
                families.Add(family);
            }
        }

        foreach (var alias in _aliases)
        {
            if (seen.Add(alias.Name))
            {
                families.Add(alias);
            }
        }

        return families;
    }

    private string NormalizeFamilyName(string? familyName)
    {
        if (string.IsNullOrWhiteSpace(familyName))
        {
            return _defaultFamily;
        }

        return IsAlias(familyName) ? _defaultFamily : familyName;
    }

    private Typeface NormaliseTypeface(Typeface typeface)
    {
        var familyName = typeface.FontFamily?.Name;
        if (!string.IsNullOrWhiteSpace(familyName) && IsAlias(familyName))
        {
            return new Typeface(_defaultFamily, typeface.Style, typeface.Weight, typeface.Stretch);
        }

        return typeface;
    }

    private bool IsAlias(string familyName)
        => _aliases.Any(alias => string.Equals(alias.Name, familyName, StringComparison.OrdinalIgnoreCase));
}

