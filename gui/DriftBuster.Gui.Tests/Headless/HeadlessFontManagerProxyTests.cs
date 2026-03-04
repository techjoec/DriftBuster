using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using Avalonia.Media;
using Avalonia.Platform;
using AwesomeAssertions;
using DriftBuster.Gui.Headless;
using Xunit;

namespace DriftBuster.Gui.Tests.Headless;

public sealed class HeadlessFontManagerProxyTests
{
    private const string DefaultFamilyName = "Inter";
    private static readonly IReadOnlyList<string> Aliases = new[] { "fonts:SystemFonts", "fonts:SystemFonts#Inter" };
    private static readonly MethodInfo TryMatchCharacterMethod = ResolveMethod("TryMatchCharacter", new[]
    {
        typeof(int), typeof(FontStyle), typeof(FontWeight), typeof(FontStretch), typeof(CultureInfo), typeof(Typeface).MakeByRefType(),
    });
    private static readonly MethodInfo TryCreateGlyphTypefaceMethod = ResolveMethod("TryCreateGlyphTypeface", new[]
    {
        typeof(string), typeof(FontStyle), typeof(FontWeight), typeof(FontStretch), typeof(IGlyphTypeface).MakeByRefType(),
    });
    private static readonly MethodInfo GetInstalledFamiliesMethod = ResolveMethod("GetInstalledFontFamilyNames", new[] { typeof(bool) });

    [Fact]
    public void TryMatchCharacter_returns_default_typeface_when_inner_throws()
    {
        var (inner, stub) = CreateStub();
        stub.TryMatchCharacterHandler = (_, _, _, _, _, _) => throw new InvalidOperationException("boom");

        var proxy = HeadlessFontManagerProxy.Create(inner, DefaultFamilyName, Aliases);
        var success = InvokeTryMatchCharacter(
            proxy,
            0x21,
            FontStyle.Normal,
            FontWeight.Normal,
            FontStretch.Normal,
            null,
            out var typeface);

        success.Should().BeTrue();
        typeface.FontFamily.Name.Should().Be(DefaultFamilyName);
    }

    [Fact]
    public void TryMatchCharacter_normalises_alias_typeface()
    {
        var (inner, stub) = CreateStub();
        stub.TryMatchCharacterHandler = (_, style, weight, stretch, _, _) =>
            (true, new Typeface("fonts:SystemFonts", style, weight, stretch));

        var proxy = HeadlessFontManagerProxy.Create(inner, DefaultFamilyName, Aliases);
        var success = InvokeTryMatchCharacter(
            proxy,
            0x22,
            FontStyle.Italic,
            FontWeight.Bold,
            FontStretch.Condensed,
            CultureInfo.InvariantCulture,
            out var typeface);

        success.Should().BeTrue();
        typeface.FontFamily.Name.Should().Be(DefaultFamilyName);
        typeface.Style.Should().Be(FontStyle.Italic);
        typeface.Weight.Should().Be(FontWeight.Bold);
        typeface.Stretch.Should().Be(FontStretch.Condensed);
    }

    [Fact]
    public void TryMatchCharacter_normalises_fragment_alias_typeface()
    {
        var (inner, stub) = CreateStub();
        stub.TryMatchCharacterHandler = (_, style, weight, stretch, _, _) =>
            (true, new Typeface("fonts:SystemFonts#Inter", style, weight, stretch));

        var proxy = HeadlessFontManagerProxy.Create(inner, DefaultFamilyName, Aliases);
        var success = InvokeTryMatchCharacter(
            proxy,
            0x23,
            FontStyle.Oblique,
            FontWeight.Light,
            FontStretch.Expanded,
            CultureInfo.InvariantCulture,
            out var typeface);

        success.Should().BeTrue();
        typeface.FontFamily.Name.Should().Be(DefaultFamilyName);
        typeface.Style.Should().Be(FontStyle.Oblique);
        typeface.Weight.Should().Be(FontWeight.Light);
        typeface.Stretch.Should().Be(FontStretch.Expanded);
    }

    [Fact]
    public void TryCreateGlyphTypeface_falls_back_to_default_family_on_failure()
    {
        var (inner, stub) = CreateStub();
        var requestedFamilies = new List<string?>();
        var glyphTypeface = CreateGlyphTypefaceStub();

        stub.TryCreateGlyphTypefaceHandler = (family, _, _, _) =>
        {
            requestedFamilies.Add(family);
            return string.Equals(family, DefaultFamilyName, StringComparison.OrdinalIgnoreCase)
                ? (true, glyphTypeface)
                : (false, null);
        };

        var proxy = HeadlessFontManagerProxy.Create(inner, DefaultFamilyName, Aliases);
        var success = InvokeTryCreateGlyphTypeface(
            proxy,
            "MissingFont",
            FontStyle.Normal,
            FontWeight.Normal,
            FontStretch.Normal,
            out var glyph);

        success.Should().BeTrue();
        requestedFamilies.Should().Contain("MissingFont");
        requestedFamilies.Should().Contain(DefaultFamilyName);
        glyph.Should().BeSameAs(glyphTypeface);
    }

    [Fact]
    public void TryCreateGlyphTypeface_normalises_fragment_alias_before_invocation()
    {
        var (inner, stub) = CreateStub();
        var requestedFamilies = new List<string?>();
        var glyphTypeface = CreateGlyphTypefaceStub();

        stub.TryCreateGlyphTypefaceHandler = (family, _, _, _) =>
        {
            requestedFamilies.Add(family);
            return string.Equals(family, DefaultFamilyName, StringComparison.OrdinalIgnoreCase)
                ? (true, glyphTypeface)
                : (false, null);
        };

        var proxy = HeadlessFontManagerProxy.Create(inner, DefaultFamilyName, Aliases);
        var success = InvokeTryCreateGlyphTypeface(
            proxy,
            "fonts:SystemFonts#Inter",
            FontStyle.Normal,
            FontWeight.Normal,
            FontStretch.Normal,
            out var glyph);

        success.Should().BeTrue();
        requestedFamilies.Should().Equal(new[] { DefaultFamilyName });
        glyph.Should().BeSameAs(glyphTypeface);
    }

    [Fact]
    public void TryCreateGlyphTypeface_returns_false_when_inner_throws()
    {
        var (inner, stub) = CreateStub();
        stub.TryCreateGlyphTypefaceHandler = (_, _, _, _) => throw new InvalidOperationException("boom");

        var proxy = HeadlessFontManagerProxy.Create(inner, DefaultFamilyName, Aliases);
        var success = InvokeTryCreateGlyphTypeface(
            proxy,
            "Inter",
            FontStyle.Normal,
            FontWeight.Normal,
            FontStretch.Normal,
            out var glyph);

        success.Should().BeFalse();
        glyph.Should().BeNull();
    }

    [Fact]
    public void GetInstalledFontFamilyNames_merges_aliases_with_inner_entries()
    {
        var (inner, stub) = CreateStub();
        var observedChecks = new List<bool>();
        stub.GetInstalledFontFamilyNamesHandler = check =>
        {
            observedChecks.Add(check);
            return new[] { DefaultFamilyName, "Custom" };
        };

        var proxy = HeadlessFontManagerProxy.Create(inner, DefaultFamilyName, Aliases);
        var result = InvokeGetInstalledFontFamilyNames(proxy, checkForUpdates: true);

        result.Should().Contain(new[]
        {
            DefaultFamilyName,
            "fonts:SystemFonts",
            "fonts:SystemFonts#Inter",
            "Custom",
        });
        observedChecks.Should().Equal(new[] { true });
    }

    [Fact]
    public void Create_throws_for_invalid_arguments()
    {
        var createWithNullInner = () => HeadlessFontManagerProxy.Create(null!, DefaultFamilyName, Aliases);
        var createWithBlankDefault = () => HeadlessFontManagerProxy.Create(CreateStub().Proxy, " ", Aliases);

        createWithNullInner.Should().Throw<ArgumentNullException>();
        createWithBlankDefault.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_accepts_null_or_blank_alias_entries()
    {
        var (inner, stub) = CreateStub();
        stub.GetInstalledFontFamilyNamesHandler = _ => Array.Empty<string>();

        var proxy = HeadlessFontManagerProxy.Create(inner, DefaultFamilyName, new[] { " ", "fonts:SystemFonts#Inter", null! });
        var result = InvokeGetInstalledFontFamilyNames(proxy, checkForUpdates: false);

        result.Should().Contain("fonts:SystemFonts");
        result.Should().Contain("Inter");
    }

    [Fact]
    public void Create_allows_null_alias_collection_and_keeps_default_aliases()
    {
        var (inner, stub) = CreateStub();
        stub.GetInstalledFontFamilyNamesHandler = _ => Array.Empty<string>();

        var proxy = HeadlessFontManagerProxy.Create(inner, DefaultFamilyName, aliases: null!);
        var result = InvokeGetInstalledFontFamilyNames(proxy, checkForUpdates: false);

        result.Should().Contain(new[] { "Inter", "fonts:SystemFonts", "fonts:SystemFonts#Inter" });
    }

    [Fact]
    public void GetDefaultFontFamilyName_returns_configured_value()
    {
        var (inner, _) = CreateStub();
        var proxy = HeadlessFontManagerProxy.Create(inner, DefaultFamilyName, Aliases);
        var method = typeof(IFontManagerImpl).GetMethod("GetDefaultFontFamilyName", Type.EmptyTypes);
        method.Should().NotBeNull();

        var value = method!.Invoke(proxy, Array.Empty<object>());
        value.Should().Be(DefaultFamilyName);
    }

    [Fact]
    public void Stream_overload_delegates_to_inner_default_invocation_path()
    {
        var (inner, _) = CreateStub();
        var proxy = HeadlessFontManagerProxy.Create(inner, DefaultFamilyName, Aliases);
        var streamMethod = typeof(IFontManagerImpl).GetMethod("TryCreateGlyphTypeface", new[]
        {
            typeof(Stream), typeof(FontSimulations), typeof(IGlyphTypeface).MakeByRefType(),
        });
        streamMethod.Should().NotBeNull();

        using var stream = new MemoryStream(new byte[] { 0x00 });
        var args = new object?[] { stream, FontSimulations.None, null };
        var success = (bool)streamMethod!.Invoke(proxy, args)!;

        success.Should().BeFalse();
        args[2].Should().BeNull();
    }

    [Fact]
    public void TryMatchCharacter_preserves_non_alias_typeface_family()
    {
        var (inner, stub) = CreateStub();
        stub.TryMatchCharacterHandler = (_, style, weight, stretch, _, _) =>
            (true, new Typeface("CustomFamily", style, weight, stretch));

        var proxy = HeadlessFontManagerProxy.Create(inner, DefaultFamilyName, Aliases);
        var success = InvokeTryMatchCharacter(
            proxy,
            0x24,
            FontStyle.Normal,
            FontWeight.Normal,
            FontStretch.Normal,
            CultureInfo.InvariantCulture,
            out var typeface);

        success.Should().BeTrue();
        typeface.FontFamily.Name.Should().Be("CustomFamily");
    }

    private static (IFontManagerImpl Proxy, StubFontManagerImpl Stub) CreateStub()
    {
        var proxy = DispatchProxy.Create<IFontManagerImpl, StubFontManagerImpl>()!;
        var stub = (StubFontManagerImpl)(object)proxy;
        return (proxy, stub);
    }

    private static IGlyphTypeface CreateGlyphTypefaceStub()
        => DispatchProxy.Create<IGlyphTypeface, GlyphTypefaceStub>()!;

    private static MethodInfo ResolveMethod(string name, Type[] parameterTypes)
    {
        var method = typeof(IFontManagerImpl).GetMethod(name, parameterTypes);
        method.Should().NotBeNull($"IFontManagerImpl.{name} must be available");
        return method!;
    }

    private static bool InvokeTryMatchCharacter(
        IFontManagerImpl proxy,
        int codepoint,
        FontStyle style,
        FontWeight weight,
        FontStretch stretch,
        CultureInfo? culture,
        out Typeface typeface)
    {
        var args = new object?[] { codepoint, style, weight, stretch, culture, null };
        var success = (bool)TryMatchCharacterMethod.Invoke(proxy, args)!;
        typeface = args[5].Should().BeOfType<Typeface>().Subject;
        return success;
    }

    private static bool InvokeTryCreateGlyphTypeface(
        IFontManagerImpl proxy,
        string family,
        FontStyle style,
        FontWeight weight,
        FontStretch stretch,
        out IGlyphTypeface? glyph)
    {
        var args = new object?[] { family, style, weight, stretch, null };
        var success = (bool)TryCreateGlyphTypefaceMethod.Invoke(proxy, args)!;
        glyph = args[4] as IGlyphTypeface;
        return success;
    }

    private static string[] InvokeGetInstalledFontFamilyNames(IFontManagerImpl proxy, bool checkForUpdates)
    {
        var args = new object?[] { checkForUpdates };
        return (string[]?)GetInstalledFamiliesMethod.Invoke(proxy, args) ?? Array.Empty<string>();
    }

    private class StubFontManagerImpl : DispatchProxy
    {
        public Func<int, FontStyle, FontWeight, FontStretch, CultureInfo, Typeface?, (bool Success, Typeface? Typeface)>? TryMatchCharacterHandler { get; set; }

        public Func<string?, FontStyle, FontWeight, FontStretch, (bool Success, IGlyphTypeface? Glyph)>? TryCreateGlyphTypefaceHandler { get; set; }

        public Func<bool, string[]?>? GetInstalledFontFamilyNamesHandler { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod is null)
            {
                return null;
            }

            args ??= Array.Empty<object?>();

            return targetMethod.Name switch
            {
                "GetDefaultFontFamilyName" => DefaultFamilyName,
                "GetInstalledFontFamilyNames" when args.Length == 1 => HandleGetInstalledFontFamilyNames(args),
                "TryMatchCharacter" when args.Length == 6 => HandleTryMatchCharacter(args),
                "TryCreateGlyphTypeface" when args.Length == 5 => HandleTryCreateGlyphTypeface(args),
                "TryCreateGlyphTypeface" when args.Length == 3 => false,
                _ => throw new NotSupportedException($"Method '{targetMethod.Name}' is not supported by the stub."),
            };
        }

        private object HandleTryMatchCharacter(object?[] args)
        {
            if (TryMatchCharacterHandler is null)
            {
                return false;
            }

            var result = TryMatchCharacterHandler.Invoke(
                args.Length > 0 && args[0] is int codepoint ? codepoint : default,
                args.Length > 1 && args[1] is FontStyle style ? style : FontStyle.Normal,
                args.Length > 2 && args[2] is FontWeight weight ? weight : FontWeight.Normal,
                args.Length > 3 && args[3] is FontStretch stretch ? stretch : FontStretch.Normal,
                args.Length > 4 && args[4] is CultureInfo culture ? culture : CultureInfo.InvariantCulture,
                args.Length > 5 && args[5] is Typeface typeface ? typeface : null);

            if (args.Length > 5)
            {
                args[5] = result.Typeface;
            }

            return result.Success;
        }

        private object HandleTryCreateGlyphTypeface(object?[] args)
        {
            if (TryCreateGlyphTypefaceHandler is null)
            {
                return false;
            }

            var result = TryCreateGlyphTypefaceHandler.Invoke(
                args.Length > 0 ? args[0] as string : null,
                args.Length > 1 && args[1] is FontStyle style ? style : FontStyle.Normal,
                args.Length > 2 && args[2] is FontWeight weight ? weight : FontWeight.Normal,
                args.Length > 3 && args[3] is FontStretch stretch ? stretch : FontStretch.Normal);

            if (args.Length > 4)
            {
                args[4] = result.Glyph;
            }

            return result.Success;
        }

        private object HandleGetInstalledFontFamilyNames(object?[] args)
        {
            if (GetInstalledFontFamilyNamesHandler is null)
            {
                return Array.Empty<string>();
            }

            var checkForUpdates = args.Length > 0 && args[0] is bool value && value;
            return GetInstalledFontFamilyNamesHandler.Invoke(checkForUpdates) ?? Array.Empty<string>();
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

            if (targetMethod.ReturnType.IsValueType)
            {
                return Activator.CreateInstance(targetMethod.ReturnType);
            }

            return null;
        }
    }
}
