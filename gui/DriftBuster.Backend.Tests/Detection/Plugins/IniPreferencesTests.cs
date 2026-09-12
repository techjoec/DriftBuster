using System.Text;

using DriftBuster.Backend.Detection.Plugins;

namespace DriftBuster.Backend.Tests.Detection.Plugins;

/// <summary>Mirror of tests/formats/test_ini_preferences.py.</summary>
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

    // signal_score below 2 with a .preferences extension returns before the classification ladder: no lineage,
    // no review flags, confidence pinned at 0.6 (interpreter-verified).
    [Fact]
    public void WeakPreferencesFileReturnsBeforeClassification()
    {
        var content = "a: 1\nb: 2\n" + string.Join("\n", Enumerable.Range(0, 8).Select(i => $"plain line {i}")) + "\n";
        var match = new IniPlugin().Detect("weak.preferences", Encoding.UTF8.GetBytes(content), content);
        match.Should().NotBeNull();
        match!.FormatName.Should().Be("ini");
        match.Variant.Should().Be("sectionless-ini");
        match.Confidence.Should().BeApproximately(0.6, 1e-9);
        match.Reasons.Should().Equal(
            "Detected utf-8 codec",
            "Detected key/value assignments typical of INI-style configuration",
            "Preferences file with colon assignments treated as INI");
        match.Metadata!.Keys.Should().Equal("encoding_info", "encoding", "key_value_pairs", "colon_separator_pairs", "comment_style", "key_density");
        match.Metadata["key_density"].Should().Be(0.2);
    }
}
