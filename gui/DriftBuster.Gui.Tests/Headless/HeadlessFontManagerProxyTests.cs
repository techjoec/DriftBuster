using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;

using Avalonia.Media;
using Avalonia.Platform;

using DriftBuster.Gui.Headless;

using FluentAssertions;

using Xunit;

namespace DriftBuster.Gui.Tests.Headless;

public sealed class HeadlessFontManagerProxyTests
{
    private const string DefaultFamilyName = "Inter";
    private static readonly IReadOnlyList<string> Aliases = new[] { "fonts:SystemFonts" };

    [Fact]
    public void TryMatchCharacter_returns_default_typeface_when_inner_throws()
    {
        var (inner, stub) = CreateStub();
        stub.TryMatchCharacterHandler = (_, _, _, _, _, _) => throw new InvalidOperationException("boom");

        var proxy = HeadlessFontManagerProxy.Create(inner, DefaultFamilyName, Aliases);
        var method = typeof(IFontManagerImpl).GetMethod("TryMatchCharacter", new[]
        {
            typeof(int), typeof(FontStyle), typeof(FontWeight), typeof(FontStretch), typeof(CultureInfo), typeof(Typeface).MakeByRefType(),
        });

        method.Should().NotBeNull();

        var arguments = new object?[]
        {
            0x21,
            FontStyle.Normal,
            FontWeight.Normal,
            FontStretch.Normal,
            null,
            null,
        };

        var success = (bool)method!.Invoke(proxy, arguments)!;

        success.Should().BeTrue();
        var typeface = arguments[5].Should().BeOfType<Typeface>().Subject;
        typeface.FontFamily.Name.Should().Be(DefaultFamilyName);
    }

    [Fact]
    public void TryMatchCharacter_normalises_alias_typeface()
    {
        var (inner, stub) = CreateStub();
        stub.TryMatchCharacterHandler = (_, style, weight, stretch, _, _) =>
        {
            var aliasTypeface = new Typeface("fonts:SystemFonts", style, weight, stretch);
            return (true, aliasTypeface);
        };

        var proxy = HeadlessFontManagerProxy.Create(inner, DefaultFamilyName, Aliases);
        var method = typeof(IFontManagerImpl).GetMethod("TryMatchCharacter", new[]
        {
            typeof(int), typeof(FontStyle), typeof(FontWeight), typeof(FontStretch), typeof(CultureInfo), typeof(Typeface).MakeByRefType(),
        });

        method.Should().NotBeNull();

        var arguments = new object?[]
        {
            0x22,
            FontStyle.Italic,
            FontWeight.Bold,
            FontStretch.Condensed,
            CultureInfo.InvariantCulture,
            null,
        };

        var success = (bool)method!.Invoke(proxy, arguments)!;

        success.Should().BeTrue();
        var typeface = arguments[5].Should().BeOfType<Typeface>().Subject;
        typeface.FontFamily.Name.Should().Be(DefaultFamilyName);
        typeface.Style.Should().Be(FontStyle.Italic);
        typeface.Weight.Should().Be(FontWeight.Bold);
        typeface.Stretch.Should().Be(FontStretch.Condensed);
    }

    [Fact]
    public void TryCreateGlyphTypeface_falls_back_to_default_family_on_failure()
    {
        var (inner, stub) = CreateStub();
        var requestedFamilies = new List<string?>();
        var glyphTypeface = CreateGlyphTypefaceStub();

        stub.TryCreateGlyphTypefaceHandler = (family, style, weight, stretch) =>
        {
            requestedFamilies.Add(family);

            if (!string.Equals(family, DefaultFamilyName, StringComparison.OrdinalIgnoreCase))
            {
                return (false, null);
            }

            return (true, glyphTypeface);
        };

        var proxy = HeadlessFontManagerProxy.Create(inner, DefaultFamilyName, Aliases);
        var method = typeof(IFontManagerImpl).GetMethod("TryCreateGlyphTypeface", new[]
        {
            typeof(string), typeof(FontStyle), typeof(FontWeight), typeof(FontStretch), typeof(IGlyphTypeface).MakeByRefType(),
        });

        method.Should().NotBeNull();

        var arguments = new object?[]
        {
            "MissingFont",
            FontStyle.Normal,
            FontWeight.Normal,
            FontStretch.Normal,
            null,
        };

        var success = (bool)method!.Invoke(proxy, arguments)!;

        success.Should().BeTrue();
        requestedFamilies.Should().Contain("MissingFont");
        requestedFamilies.Should().Contain(DefaultFamilyName);
        arguments[4].Should().BeSameAs(glyphTypeface);
    }

    private static (IFontManagerImpl Proxy, StubFontManagerImpl Stub) CreateStub()
    {
        var proxy = DispatchProxy.Create<IFontManagerImpl, StubFontManagerImpl>()!;
        var stub = (StubFontManagerImpl)(object)proxy;
        return (proxy, stub);
    }

    private static IGlyphTypeface CreateGlyphTypefaceStub()
        => DispatchProxy.Create<IGlyphTypeface, GlyphTypefaceStub>()!;

    private class StubFontManagerImpl : DispatchProxy
    {
        public Func<int, FontStyle, FontWeight, FontStretch, CultureInfo, Typeface?, (bool Success, Typeface? Typeface)>? TryMatchCharacterHandler { get; set; }

        public Func<string?, FontStyle, FontWeight, FontStretch, (bool Success, IGlyphTypeface? Glyph)>? TryCreateGlyphTypefaceHandler { get; set; }

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
                "GetInstalledFontFamilyNames" => Array.Empty<string>(),
                "TryMatchCharacter" when args.Length == 6 => HandleTryMatchCharacter(args),
                "TryCreateGlyphTypeface" when args.Length == 5 => HandleTryCreateGlyphTypeface(args),
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
