using DriftBuster.Backend.Detection.Plugins;

namespace DriftBuster.Backend.Detection;

/// <summary>Registers the built-in format plugins into <see cref="FormatRegistry.Default"/> once and exposes them.</summary>
public static class DefaultPlugins
{
    private static readonly Lazy<FormatRegistry> Registered = new(RegisterAll);

    private static FormatRegistry RegisterAll()
    {
        var registry = FormatRegistry.Default;
        foreach (var plugin in CreateBuiltIns())
        {
            registry.Register(plugin);
        }

        return registry;
    }

    /// <summary>Fresh instances of every built-in plugin, in registration order.</summary>
    public static IReadOnlyList<IFormatPlugin> CreateBuiltIns() =>
    [
        new TextPlugin(),
    ];

    /// <summary>The default registry with every built-in plugin registered.</summary>
    public static FormatRegistry Registry => Registered.Value;

    /// <summary>A snapshot of the registered built-in plugins in registration order.</summary>
    public static IReadOnlyList<IFormatPlugin> GetPlugins() => Registry.GetPlugins();
}
