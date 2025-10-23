using System;
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
            if (Resolve(locator, typeof(IFontManagerImpl)) is not IFontManagerImpl fontManager)
            {
                fontManager = CreateFontManager();
                Register(locator, typeof(IFontManagerImpl), fontManager);
            }

            BindFontOptions(locator);

            if (Application.Current is App app)
            {
                App.EnsureFontResources(app);
            }
        }
    }

    private static void BindFontOptions(AvaloniaLocator locator)
    {
        if (Resolve(locator, typeof(FontManagerOptions)) is FontManagerOptions existing)
        {
            existing.DefaultFamilyName = DefaultFamilyName;
            existing.FontFallbacks = new[]
            {
                new FontFallback
                {
                    FontFamily = new FontFamily(DefaultFamilyName),
                }
            };

            return;
        }

        var options = new FontManagerOptions
        {
            DefaultFamilyName = DefaultFamilyName,
            FontFallbacks = new[]
            {
                new FontFallback
                {
                    FontFamily = new FontFamily(DefaultFamilyName),
                }
            }
        };

        Register(locator, typeof(FontManagerOptions), options);
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
}
