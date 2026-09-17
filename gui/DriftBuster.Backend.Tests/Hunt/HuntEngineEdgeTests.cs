using DriftBuster.Backend.Hunt;

namespace DriftBuster.Backend.Tests.Hunt;

/// <summary>Plan transforms built from hunt hits and the placeholder template format.</summary>
public sealed class HuntEngineEdgeTests
{
    private static readonly HuntRule TokenRule = new("rule", "description", "token");

    [Fact]
    public void PlanTransformFallsBackToTheExcerptWithoutMatches()
    {
        var hit = new HuntFinding(TokenRule, "/p", 3, "excerpt text", []);

        var transform = HuntEngine.PlanTransformForHit(hit, HuntEngine.DefaultPlaceholderTemplate);

        transform.Should().Be(new PlanTransform("token", "excerpt text", "{{ token }}", "rule", "/p", 3, "excerpt text"));
    }

    [Fact]
    public void PlanTransformPrefersTheFirstMatchWithAMarkerElseTheFirstMatch()
    {
        HuntEngine.PlanTransformForHit(new HuntFinding(TokenRule, "/p", 1, "e", ["plain", "has:colon", "a.b"]), "{token_name}")!.Value
            .Should().Be("has:colon");
        HuntEngine.PlanTransformForHit(new HuntFinding(TokenRule, "/p", 1, "e", ["plain", "other"]), "{token_name}")!.Value
            .Should().Be("plain");
    }

    [Fact]
    public void PlanTransformNeedsATokenAndAValue()
    {
        HuntEngine.PlanTransformForHit(new HuntFinding(new HuntRule("r", "d", "  "), "/p", 1, "e", ["x"]), "{token_name}").Should().BeNull();
        HuntEngine.PlanTransformForHit(new HuntFinding(TokenRule, "/p", 1, string.Empty, []), "{token_name}").Should().BeNull();
    }

    [Fact]
    public void BuildPlanTransformsDeduplicatesByTokenValuePathAndLine()
    {
        var hits = new[]
        {
            new HuntFinding(TokenRule, "/p", 1, "e", ["a.b"]),
            new HuntFinding(TokenRule, "/p", 1, "other excerpt", ["a.b"]),
            new HuntFinding(TokenRule, "/p", 2, "e", ["a.b"]),
            new HuntFinding(new HuntRule("r", "d"), "/p", 3, "e", ["a.b"]),
        };

        HuntEngine.BuildPlanTransforms(hits).Select(transform => transform.LineNumber).Should().Equal(1, 2);
    }

    [Theory]
    [InlineData("{{{{ {token_name} }}}}", "name", "{{ name }}")]
    [InlineData("<<{token_name}>>", "name", "<<name>>")]
    [InlineData("{{literal}}", "name", "{literal}")]
    [InlineData("plain", "name", "plain")]
    [InlineData("{token_name,12}", "tok", "         tok")]
    [InlineData("{token_name:>12}", "tok", null)]
    public void FormatPlaceholderUsesCompositeFormatting(string template, string value, string? expected)
    {
        var format = () => HuntEngine.FormatPlaceholder(template, value);

        if (expected is null)
        {
            format.Should().Throw<FormatException>();
            return;
        }

        format().Should().Be(expected);
    }
}
