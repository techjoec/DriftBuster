using System.Text;

using DriftBuster.Backend.Detection.Plugins;

namespace DriftBuster.Backend.Tests.Detection.Plugins;

/// <summary>The yaml plugin's top-level key collection stopping at a break.</summary>
public sealed class YamlKeysBreakTests
{
    [Fact]
    public void YamlTopKeysPreviewBreaksAtEight()
    {
        // Build 10 top-level keys to trigger the preview break logic
        var content = string.Join("\n", Enumerable.Range(0, 10).Select(index => $"k{index}: v{index}"));
        var match = new YamlPlugin().Detect("many.yaml", Encoding.UTF8.GetBytes(content), content);
        match.Should().NotBeNull();
        var keys = YamlPluginTests.Strings(match!.Metadata!["top_level_keys_preview"]).ToList();
        keys.Should().HaveCount(8); // preview caps at 8 and breaks
        keys.Should().Equal("k0", "k1", "k2", "k3", "k4", "k5", "k6", "k7");
        match.Confidence.Should().BeApproximately(0.75, 1e-9);
    }
}
