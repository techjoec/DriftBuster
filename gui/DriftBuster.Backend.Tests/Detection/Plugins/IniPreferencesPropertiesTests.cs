using System.Text;

using DriftBuster.Backend.Detection;
using DriftBuster.Backend.Detection.Plugins;

namespace DriftBuster.Backend.Tests.Detection.Plugins;

/// <summary>Mirror of tests/formats/test_ini_preferences_properties.py.</summary>
public sealed class IniPreferencesPropertiesTests
{
    private static DetectionMatch? Detect(string name, string content)
        => new IniPlugin().Detect(name, Encoding.UTF8.GetBytes(content), content);

    private static OrderedDictionary<string, object?> Lineage(DetectionMatch match)
        => match.Metadata!["detector_lineage"].Should().BeOfType<OrderedDictionary<string, object?>>().Subject;

    [Fact]
    public void IniPreferencesFallbackSectionless()
    {
        // Colon-only assignments with a .preferences extension should classify.
        var match = Detect("settings.preferences", "a: 1\nb: 2\n");
        match.Should().NotBeNull();
        match!.FormatName.Should().Be("ini");
        match.Variant.Should().Be("sectionless-ini");
        match.Metadata.Should().NotBeNullOrEmpty();
        Lineage(match)["variant"].Should().Be("sectionless-ini");
        match.Confidence.Should().BeApproximately(0.55, 1e-9);
        match.Reasons.Should().Equal(
            "Detected utf-8 codec",
            "Detected key/value assignments typical of INI-style configuration",
            "Key/value pairs without sections default to sectionless INI interpretation");
    }

    [Fact]
    public void PropertiesBasicJavaPropertiesRecognition()
    {
        // A simple Java properties file with key/value pairs should classify.
        var match = Detect("application.properties", "a=b\nc=d\n");
        match.Should().NotBeNull();
        match!.FormatName.Should().Be("ini");
        match.Variant.Should().Be("java-properties");
        match.Metadata.Should().NotBeNullOrEmpty();
        Lineage(match)["variant"].Should().Be("java-properties");
        match.Confidence.Should().BeApproximately(0.65, 1e-9);
    }
}
