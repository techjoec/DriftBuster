using System.Text;

using DriftBuster.Backend.Detection;
using DriftBuster.Backend.Detection.Plugins;

namespace DriftBuster.Backend.Tests.Detection.Plugins;

/// <summary>The json plugin's review flags.</summary>
public sealed class JsonFlagsTests
{
    private static DetectionMatch? Detect(string name, string content)
        => new JsonPlugin().Detect(name, Encoding.UTF8.GetBytes(content), content);

    [Fact]
    public void JsonParseFailedFlag()
    {
        const string content = """{"a": 1,}"""; // trailing comma, no comments
        var match = Detect("config.json", content);
        match.Should().NotBeNull();
        match!.Metadata.Should().NotBeNull();
        match.Metadata!["needs_review"].Should().Be(true);
        match.Metadata["parse_failed"].Should().Be(true);
        var reviewReasons = match.Metadata["review_reasons"].Should().BeAssignableTo<IEnumerable<string>>().Subject;
        reviewReasons.Should().Contain(reason => reason.Contains("parse failed", StringComparison.OrdinalIgnoreCase));
        reviewReasons.Should().Equal("JSON parse failed under sample");
        match.Confidence.Should().BeApproximately(0.8500000000000001, 1e-9);
    }
}
