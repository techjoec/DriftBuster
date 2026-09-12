using System.Text;

using DriftBuster.Backend.Detection;
using DriftBuster.Backend.Detection.Plugins;

namespace DriftBuster.Backend.Tests.Detection.Plugins;

/// <summary>Mirror of tests/formats/test_ini_flags.py.</summary>
public sealed class IniFlagsTests
{
    private static DetectionMatch? Detect(string name, string content)
        => new IniPlugin().Detect(name, Encoding.UTF8.GetBytes(content), content);

    private static OrderedDictionary<string, object?> Mapping(object? value)
        => value.Should().BeOfType<OrderedDictionary<string, object?>>().Subject;

    [Fact]
    public void IniMalformedSectionFlag()
    {
        const string content = "[ok]\n    a=1\n    [bad section\n    b=2";
        var match = Detect("settings.ini", content);
        // Depending on signals, may detect as ini or none; assert the flag whenever detected (it is, here).
        match.Should().NotBeNull();
        match!.Metadata.Should().NotBeNull();
        match.Metadata!["needs_review"].Should().Be(true);
        var reviewReasons = match.Metadata["review_reasons"].Should().BeAssignableTo<IEnumerable<string>>().Subject;
        reviewReasons.Should().Contain(reason => reason.Contains("Malformed section", StringComparison.Ordinal));
        var lineage = Mapping(match.Metadata["detector_lineage"]);
        Mapping(lineage["signals"])["has_sections"].Should().Be(true);
        reviewReasons.Should().Equal("Malformed section header without closing bracket");
        match.Confidence.Should().BeApproximately(0.8250000000000001, 1e-9);
    }

    [Fact]
    public void IniColonOnlyFlagOutsideProperties()
    {
        const string content = "key: value\n    other: 2";
        var match = Detect("settings.conf", content);
        match.Should().NotBeNull();
        match!.Metadata.Should().NotBeNull();
        match.Metadata!["needs_review"].Should().Be(true);
        var reviewReasons = match.Metadata["review_reasons"].Should().BeAssignableTo<IEnumerable<string>>().Subject;
        reviewReasons.Should().Contain(reason => reason.Contains("Colon-only", StringComparison.Ordinal));
        var lineage = Mapping(match.Metadata["detector_lineage"]);
        Mapping(lineage["signals"])["has_sections"].Should().Be(false);
        match.Variant.Should().Be("sectionless-ini");
        match.Confidence.Should().BeApproximately(0.65, 1e-9);
    }
}
