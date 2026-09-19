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

    /// <summary>Fresh instances of every built-in plugin in priority order (each class declares its <c>Priority</c>).</summary>
    public static IReadOnlyList<IFormatPlugin> CreateBuiltIns() =>
    [
        new RegistryExportPlugin(),
        new RegistryLivePlugin(),
        new ScriptPlugin(),
        new XmlPlugin(),
        new DockerfilePlugin(),
        new ConfPlugin(),
        new HclPlugin(),
        new YamlPlugin(),
        new TomlPlugin(),
        new IniPlugin(),
        new JsonPlugin(),
        new BinaryHybridPlugin(),
        new TextPlugin(),
    ];

    public static FormatRegistry Registry => Registered.Value;

    public static IReadOnlyList<IFormatPlugin> GetPlugins() => Registry.GetPlugins();
}
