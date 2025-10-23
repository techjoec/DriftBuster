using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;

using Avalonia.Media;
using Avalonia.Platform;

namespace DriftBuster.Gui.Headless;

internal class HeadlessFontManagerProxy : DispatchProxy
{
    private IFontManagerImpl _inner = default!;
    private string _defaultFamilyName = string.Empty;
    private HashSet<string> _aliases = default!;

    internal static IFontManagerImpl Create(IFontManagerImpl inner, string defaultFamilyName, IEnumerable<string> aliases)
    {
        if (inner is null)
        {
            throw new ArgumentNullException(nameof(inner));
        }

        if (string.IsNullOrWhiteSpace(defaultFamilyName))
        {
            throw new ArgumentException("Default family name must be provided.", nameof(defaultFamilyName));
        }

        var proxy = Create<IFontManagerImpl, HeadlessFontManagerProxy>();
        if (proxy is null)
        {
            throw new InvalidOperationException("DispatchProxy failed to create the font manager proxy instance.");
        }

        var typed = (HeadlessFontManagerProxy)(object)proxy;
        typed.Initialise(inner, defaultFamilyName, aliases);
        return proxy!;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod is null)
        {
            return null;
        }

        args ??= Array.Empty<object?>();

        return targetMethod.Name switch
        {
            "GetDefaultFontFamilyName" => _defaultFamilyName,
            "GetInstalledFontFamilyNames" => GetInstalledFontFamilies(args),
            "TryMatchCharacter" when targetMethod.GetParameters().Length == 6
                => TryMatchCharacter(args),
            "TryCreateGlyphTypeface" when targetMethod.GetParameters().Length == 5
                => TryCreateGlyphTypeface(args),
            _ => targetMethod.Invoke(_inner, args),
        };
    }

    private void Initialise(IFontManagerImpl inner, string defaultFamilyName, IEnumerable<string> aliases)
    {
        _inner = inner;
        _defaultFamilyName = defaultFamilyName;
        _aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            defaultFamilyName,
            "fonts:SystemFonts",
        };

        if (aliases is null)
        {
            return;
        }

        foreach (var alias in aliases)
        {
            var normalized = alias?.Trim();
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                _aliases.Add(normalized);
            }
        }
    }

    private object GetInstalledFontFamilies(object?[] args)
    {
        var checkForUpdates = args.Length > 0 && args[0] is bool value && value;
        var forwarded = new object?[] { checkForUpdates };
        var method = typeof(IFontManagerImpl).GetMethod("GetInstalledFontFamilyNames", new[] { typeof(bool) });
        var result = method is not null && _inner is not null
            ? (string[]?)method.Invoke(_inner, forwarded)
            : null;

        var families = result ?? Array.Empty<string>();

        return families
            .Concat(_aliases)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private object TryMatchCharacter(object?[] args)
    {
        var forwarded = (object?[])args.Clone();
        if (forwarded.Length > 4 && forwarded[4] is null)
        {
            forwarded[4] = CultureInfo.InvariantCulture;
        }

        var method = typeof(IFontManagerImpl).GetMethod("TryMatchCharacter", new[]
        {
            typeof(int), typeof(FontStyle), typeof(FontWeight), typeof(FontStretch), typeof(CultureInfo), typeof(Typeface).MakeByRefType(),
        });

        var success = false;
        if (method is not null && _inner is not null)
        {
            try
            {
                success = (bool)method.Invoke(_inner, forwarded)!;
            }
            catch (TargetInvocationException)
            {
                success = false;
                ResetOutParameter(forwarded, 5);
            }
            catch (Exception)
            {
                success = false;
                ResetOutParameter(forwarded, 5);
            }
        }

        var style = forwarded.Length > 1 && forwarded[1] is FontStyle fontStyle ? fontStyle : FontStyle.Normal;
        var weight = forwarded.Length > 2 && forwarded[2] is FontWeight fontWeight ? fontWeight : FontWeight.Normal;
        var stretch = forwarded.Length > 3 && forwarded[3] is FontStretch fontStretch ? fontStretch : FontStretch.Normal;

        var normalized = NormalizeTypeface(success && forwarded.Length > 5 && forwarded[5] is Typeface captured
            ? captured
            : null, style, weight, stretch);

        if (!success)
        {
            success = true;
        }

        if (args.Length > 5)
        {
            args[5] = normalized;
        }

        return success;
    }

    private object TryCreateGlyphTypeface(object?[] args)
    {
        var method = typeof(IFontManagerImpl).GetMethod("TryCreateGlyphTypeface", new[]
        {
            typeof(string), typeof(FontStyle), typeof(FontWeight), typeof(FontStretch), typeof(IGlyphTypeface).MakeByRefType(),
        });

        var forwarded = (object?[])args.Clone();
        forwarded[0] = NormalizeFamilyName(forwarded.Length > 0 ? forwarded[0] as string : null);

        var success = InvokeGlyphTypeface(method, forwarded);

        if (!success && forwarded.Length > 0 && !string.Equals((string?)forwarded[0], _defaultFamilyName, StringComparison.OrdinalIgnoreCase))
        {
            forwarded[0] = _defaultFamilyName;
            success = InvokeGlyphTypeface(method, forwarded);
        }

        if (args.Length > 4)
        {
            args[4] = forwarded.Length > 4 ? forwarded[4] : null;
        }

        return success;
    }

    private string NormalizeFamilyName(string? familyName)
    {
        var candidate = familyName?.Trim();

        return string.IsNullOrWhiteSpace(candidate) || _aliases.Contains(candidate)
            ? _defaultFamilyName
            : candidate;
    }

    private bool InvokeGlyphTypeface(MethodInfo? method, object?[] forwarded)
    {
        if (method is null || _inner is null)
        {
            return false;
        }

        try
        {
            return (bool)method.Invoke(_inner, forwarded)!;
        }
        catch (TargetInvocationException)
        {
            ResetOutParameter(forwarded, 4);
            return false;
        }
        catch (Exception)
        {
            ResetOutParameter(forwarded, 4);
            return false;
        }
    }

    private static void ResetOutParameter(object?[] arguments, int index)
    {
        if (arguments.Length > index)
        {
            arguments[index] = null;
        }
    }

    private Typeface NormalizeTypeface(Typeface? typeface, FontStyle style, FontWeight weight, FontStretch stretch)
    {
        if (typeface is null)
        {
            return new Typeface(_defaultFamilyName, style, weight, stretch);
        }

        var value = typeface.Value;
        var familyName = value.FontFamily.Name;
        if (string.IsNullOrWhiteSpace(familyName) || _aliases.Contains(familyName))
        {
            return new Typeface(_defaultFamilyName, value.Style, value.Weight, value.Stretch);
        }

        return value;
    }
}
