using DriftBuster.Backend.Detection;
using DriftBuster.Backend.Detection.Plugins;

namespace DriftBuster.Backend.Tests.Detection;

/// <summary>
/// Mirror of tests/formats/test_plugin_guards.py. Python checks yaml, toml, text, dockerfile, hcl, conf and
/// registry_live; the array below grows as phases 2 and 3 port the remaining six.
/// </summary>
public sealed class PluginGuardTests
{
    [Fact]
    public void PluginsReturnNoneWhenTextIsNone()
    {
        const string path = "x";
        IFormatPlugin[] plugins = [new TextPlugin()];
        foreach (var plugin in plugins)
        {
            plugin.Detect(path, "{}"u8.ToArray(), null).Should().BeNull(plugin.Name);
        }
    }
}
