using DriftBuster.Backend.Detection;
using DriftBuster.Backend.Detection.Plugins;

namespace DriftBuster.Backend.Tests.Detection;

/// <summary>
/// The text guards of the yaml, toml, text, dockerfile, hcl, conf, registry_live, ini and json plugins. binary-hybrid is not
/// text-gated.
/// </summary>
public sealed class PluginGuardTests
{
    [Fact]
    public void PluginsReturnNoneWhenTextIsNone()
    {
        const string path = "x";
        IFormatPlugin[] plugins =
        [
            new YamlPlugin(),
            new TomlPlugin(),
            new TextPlugin(),
            new DockerfilePlugin(),
            new HclPlugin(),
            new ConfPlugin(),
            new IniPlugin(),
            new JsonPlugin(),
            new RegistryLivePlugin(),
        ];
        foreach (var plugin in plugins)
        {
            plugin.Detect(path, "{}"u8.ToArray(), null).Should().BeNull(plugin.Name);
        }
    }
}
