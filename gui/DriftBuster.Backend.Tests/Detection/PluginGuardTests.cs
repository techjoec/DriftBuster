using DriftBuster.Backend.Detection;
using DriftBuster.Backend.Detection.Plugins;

namespace DriftBuster.Backend.Tests.Detection;

/// <summary>
/// Mirror of tests/formats/test_plugin_guards.py. Python checks yaml, toml, text, dockerfile, hcl, conf and
/// registry_live; the port also covers ini and json, and registry_live joins the array when phase 3 ports it.
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
        ];
        foreach (var plugin in plugins)
        {
            plugin.Detect(path, "{}"u8.ToArray(), null).Should().BeNull(plugin.Name);
        }
    }
}
