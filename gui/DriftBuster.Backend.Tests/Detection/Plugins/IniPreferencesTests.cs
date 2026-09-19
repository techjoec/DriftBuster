using System.Text;
using System.Text.Json.Nodes;

using DriftBuster.Backend.Detection.Plugins;

namespace DriftBuster.Backend.Tests.Detection.Plugins;

/// <summary>The ini plugin on preferences files.</summary>
public sealed class IniPreferencesTests
{
    [Fact]
    public void ColonOnlyPreferencesShortFile()
    {
        const string content = "\n    gui.column.format:\n        \"No.\", \"%m\",\n        \"Time\", \"%Yt\"\n    gui.layout_type: 3\n    ";
        var plugin = new IniPlugin();
        var match = plugin.Detect("bluetooth.preferences", Encoding.UTF8.GetBytes(content), content);
        match.Should().NotBeNull();
        match!.FormatName.Should().Be("ini");
        match.Variant.Should().Be("sectionless-ini");
        match.Metadata.Should().NotBeNullOrEmpty();
        var lineage = match.Metadata!["detector_lineage"].Should().BeOfType<JsonObject>().Subject;
        lineage["variant"].ShouldBeJson("sectionless-ini");
        lineage["signal_score"].ShouldBeJson(2);
        match.Confidence.Should().BeApproximately(0.525, 1e-9);
        match.Metadata["key_value_pairs"].ShouldBeJson(2);
    }
}
