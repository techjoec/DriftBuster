using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

using Avalonia;
using Avalonia.Media;
using Avalonia.Platform;

namespace DriftBuster.Gui.Headless;

internal static class HeadlessFontBootstrapper
{
    private const string DefaultFamilyName = "Inter";
    private static readonly object Sync = new();

    private static readonly PropertyInfo? CurrentMutableProperty = typeof(AvaloniaLocator)
        .GetProperty("CurrentMutable", BindingFlags.Public | BindingFlags.Static);
    private static readonly MethodInfo? GetServiceMethod = typeof(AvaloniaLocator)
        .GetMethod("GetService", BindingFlags.Instance | BindingFlags.Public, new[] { typeof(Type) });
    private static readonly MethodInfo? BindMethod = typeof(AvaloniaLocator)
        .GetMethod("Bind", BindingFlags.Instance | BindingFlags.Public);
    private static readonly Uri SystemFontsUri = typeof(FontManager)
        .GetField("SystemFontsKey", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(null)
            as Uri ?? new Uri("fonts:SystemFonts", UriKind.Absolute);
    private static readonly MethodInfo? AddFontCollectionMethod = typeof(FontManager)
        .GetMethod("AddFontCollection", BindingFlags.Instance | BindingFlags.NonPublic, new[] { typeof(Avalonia.Media.Fonts.IFontCollection) });

    public static void Ensure(AppBuilder builder)
    {
        if (builder is null)
        {
            throw new ArgumentNullException(nameof(builder));
        }

        if (TryGetCurrentLocator() is not AvaloniaLocator locator)
        {
            return;
        }

        lock (Sync)
        {
            var aliases = new[]
            {
                DefaultFamilyName,
                "fonts:SystemFonts",
                $"fonts:SystemFonts#{DefaultFamilyName}",
            };

            var proxy = HeadlessFontManagerProxy.Create(CreateFontManager(), DefaultFamilyName, aliases);
            Register(locator, typeof(IFontManagerImpl), proxy);

            BindFontOptions(locator);

            var existingFontManager = Resolve(locator, typeof(FontManager)) as FontManager;
            if (existingFontManager is IDisposable disposable)
            {
                disposable.Dispose();
            }

            var fontManager = new FontManager(proxy);
            Register(locator, typeof(FontManager), fontManager);
            fontManager = FontManager.Current;

            var bootstrapperFailures = new List<string>();
            TryRefreshSystemFontCollection(fontManager, bootstrapperFailures);
            if (!ReferenceEquals(fontManager, FontManager.Current))
            {
                bootstrapperFailures.Add("current_font_manager_mismatch");
            }

            IDictionary<string, FontFamily>? fontsResource = null;
            if (Application.Current is App app)
            {
                App.EnsureFontResources(app);
                fontsResource = TryGetFontResourceDictionary(app);
            }

            HeadlessFontBootstrapperDiagnostics.RecordSnapshot(CreateSnapshot(fontManager, fontsResource, bootstrapperFailures));
        }
    }

    private static readonly string[] RequiredFallbackFamilies =
    {
        DefaultFamilyName,
        "fonts:SystemFonts",
        "fonts:SystemFonts#Inter",
    };

    private static void BindFontOptions(AvaloniaLocator locator)
    {
        if (Resolve(locator, typeof(FontManagerOptions)) is FontManagerOptions existing)
        {
            existing.DefaultFamilyName = DefaultFamilyName;
            existing.FontFallbacks = CreateFallbacks(existing.FontFallbacks);

            return;
        }

        var options = new FontManagerOptions
        {
            DefaultFamilyName = DefaultFamilyName,
            FontFallbacks = CreateFallbacks(null),
        };

        Register(locator, typeof(FontManagerOptions), options);
    }

    private static FontFallback[] CreateFallbacks(IEnumerable<FontFallback>? existing)
    {
        var fallbacks = new List<FontFallback>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var familyName in RequiredFallbackFamilies)
        {
            AddFallback(fallbacks, seen, familyName);
        }

        if (existing is not null)
        {
            foreach (var fallback in existing)
            {
                if (fallback?.FontFamily is null)
                {
                    continue;
                }

                AddFallback(fallbacks, seen, fallback.FontFamily.Name, fallback.FontFamily);
            }
        }

        return fallbacks.ToArray();
    }

    private static void AddFallback(ICollection<FontFallback> fallbacks, ISet<string> seen, string? familyName, FontFamily? family = null)
    {
        if (string.IsNullOrWhiteSpace(familyName))
        {
            return;
        }

        if (!seen.Add(familyName))
        {
            return;
        }

        fallbacks.Add(new FontFallback
        {
            FontFamily = family ?? new FontFamily(familyName),
        });
    }

    private static object? Resolve(AvaloniaLocator locator, Type serviceType)
    {
        if (GetServiceMethod is null)
        {
            return null;
        }

        return GetServiceMethod.Invoke(locator, new object[] { serviceType });
    }

    private static void Register(AvaloniaLocator locator, Type serviceType, object instance)
    {
        if (BindMethod is null)
        {
            return;
        }

        var generic = BindMethod.MakeGenericMethod(serviceType);
        var registration = generic.Invoke(locator, Array.Empty<object>());
        if (registration is null)
        {
            return;
        }

        var toConstant = registration.GetType().GetMethod("ToConstant", BindingFlags.Instance | BindingFlags.Public, new[] { serviceType });
        toConstant?.Invoke(registration, new[] { instance });
    }

    private static IFontManagerImpl CreateFontManager()
    {
        var type = Type.GetType("Avalonia.Skia.FontManagerImpl, Avalonia.Skia");
        if (type is null)
        {
            throw new InvalidOperationException("Avalonia.Skia.FontManagerImpl could not be located for headless fallback.");
        }

        return (IFontManagerImpl)Activator.CreateInstance(type)!;
    }

    private static AvaloniaLocator? TryGetCurrentLocator()
        => CurrentMutableProperty?.GetValue(null) as AvaloniaLocator;

    private static IDictionary<string, FontFamily>? TryGetFontResourceDictionary(Application app)
    {
        const string key = "fonts:SystemFonts";

        if (!app.Resources.TryGetValue(key, out var resource))
        {
            return null;
        }

        return resource as IDictionary<string, FontFamily>;
    }

    private static readonly FieldInfo? FontCollectionsField = typeof(FontManager)
        .GetField("_fontCollections", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly PropertyInfo? PlatformImplProperty = typeof(FontManager)
        .GetProperty("PlatformImpl", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

    private static void TryRefreshSystemFontCollection(FontManager fontManager, ICollection<string> failures)
    {
        if (FontCollectionsField?.GetValue(fontManager) is not IDictionary<Uri, Avalonia.Media.Fonts.IFontCollection> dictionary)
        {
            failures.Add("font_collection_field_unavailable");
            return;
        }

        try
        {
            var keys = dictionary.Keys
                .Select(key => key?.ToString() ?? string.Empty)
                .ToArray();
            failures.Add($"existing_keys:{string.Join('|', keys)}");
        }
        catch (Exception ex)
        {
            failures.Add($"enumerate_keys_failed:{ex.GetType().Name}");
        }

        var systemFontsKey = SystemFontsUri;

        if (dictionary is ConcurrentDictionary<Uri, Avalonia.Media.Fonts.IFontCollection> concurrent)
        {
            if (!concurrent.TryRemove(systemFontsKey, out _))
            {
                failures.Add("system_fonts_key_missing_before_replace");
            }
        }
        else if (dictionary.ContainsKey(systemFontsKey))
        {
            dictionary.Remove(systemFontsKey);
        }
        else
        {
            failures.Add("system_fonts_key_not_found_before_replace");
        }

        var type = Type.GetType("Avalonia.Media.Fonts.SystemFontCollection, Avalonia.Base");
        if (type is null)
        {
            failures.Add("system_font_collection_type_missing");
            return;
        }

        if (Activator.CreateInstance(type, fontManager) is not Avalonia.Media.Fonts.IFontCollection inner)
        {
            failures.Add("system_font_collection_create_failed");
            return;
        }

        if (PlatformImplProperty?.GetValue(fontManager) is not IFontManagerImpl platformImpl)
        {
            failures.Add("platform_impl_unavailable");
            return;
        }

        inner.Initialize(platformImpl);
        var collection = new HeadlessAliasFontCollection(inner, DefaultFamilyName);
        failures.Add($"collection_key:{collection.Key}");

        var added = false;
        if (AddFontCollectionMethod is not null)
        {
            try
            {
                AddFontCollectionMethod.Invoke(fontManager, new object[] { collection });
                added = true;
            }
            catch (Exception ex)
            {
                failures.Add($"add_font_collection_failed:{ex.GetType().Name}");
            }
        }

        if (!added)
        {
            dictionary[systemFontsKey] = collection;
        }

        if (!dictionary.ContainsKey(systemFontsKey))
        {
            failures.Add("system_fonts_key_missing_after_replace");
        }
        else
        {
            try
            {
                var postKeys = dictionary.Keys
                    .Select(key => key?.ToString() ?? string.Empty)
                    .ToArray();
                failures.Add($"post_keys:{string.Join('|', postKeys)}");
            }
            catch (Exception ex)
            {
                failures.Add($"enumerate_post_keys_failed:{ex.GetType().Name}");
            }
        }

        try
        {
            _ = fontManager.SystemFonts;
        }
        catch (Exception ex)
        {
            failures.Add($"system_fonts_access_failed:{ex.GetType().Name}");
        }
    }

    private static HeadlessFontBootstrapperDiagnostics.ProbeSnapshot CreateSnapshot(
        FontManager? fontManager,
        IDictionary<string, FontFamily>? fontsResource,
        IEnumerable<string> bootstrapperFailures)
    {
        var probes = new List<HeadlessFontBootstrapperDiagnostics.ProbeResult>();
        var failureNotes = new List<string>();

        if (bootstrapperFailures is not null)
        {
            foreach (var failure in bootstrapperFailures)
            {
                if (!string.IsNullOrWhiteSpace(failure))
                {
                    failureNotes.Add($"bootstrap:{failure.Trim()}");
                }
            }
        }

        foreach (var alias in RequiredFallbackFamilies.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var result = ProbeAlias(fontManager, alias);
            probes.Add(result);

            if (!result.Success)
            {
                failureNotes.Add($"glyph:{alias}:{result.Error ?? "unknown"}");
            }
        }

        var containsSystemFonts = fontsResource?.ContainsKey("fonts:SystemFonts") ?? false;
        var containsInter = fontsResource?.Keys.Any(key =>
            string.Equals(key, "Inter", StringComparison.OrdinalIgnoreCase)) ?? false;
        var resourceCount = fontsResource?.Count ?? 0;

        return new HeadlessFontBootstrapperDiagnostics.ProbeSnapshot(
            DateTimeOffset.UtcNow,
            probes,
            containsSystemFonts,
            containsInter,
            resourceCount,
            failureNotes.Count == 0 ? null : string.Join(";", failureNotes));
    }

    private static HeadlessFontBootstrapperDiagnostics.ProbeResult ProbeAlias(FontManager? fontManager, string alias)
    {
        if (fontManager is null)
        {
            return new HeadlessFontBootstrapperDiagnostics.ProbeResult(alias, false, null, "font_manager_unavailable");
        }

        try
        {
            var success = fontManager.TryGetGlyphTypeface(new Typeface(alias), out var glyphTypeface);
            var family = glyphTypeface?.FamilyName;
            var error = success ? null : "try_get_glyph_failed";

            return new HeadlessFontBootstrapperDiagnostics.ProbeResult(alias, success, family, error);
        }
        catch (Exception ex)
        {
            return new HeadlessFontBootstrapperDiagnostics.ProbeResult(
                alias,
                false,
                null,
                $"{ex.GetType().Name}: {ex.Message}");
        }
    }
}
