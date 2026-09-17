using System.Text;

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
        var lineage = match.Metadata!["detector_lineage"].Should().BeOfType<OrderedDictionary<string, object?>>().Subject;
        lineage["variant"].Should().Be("sectionless-ini");
        lineage["signal_score"].Should().Be(2);
        match.Confidence.Should().BeApproximately(0.525, 1e-9);
        match.Metadata["key_value_pairs"].Should().Be(2);
    }
}
