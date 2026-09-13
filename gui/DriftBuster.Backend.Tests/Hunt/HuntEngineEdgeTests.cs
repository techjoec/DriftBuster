using DriftBuster.Backend.Hunt;

namespace DriftBuster.Backend.Tests.Hunt;

/// <summary>Branches of <c>_plan_transform_for_hit</c> and <c>str.format</c> that the mirrored Python tests do not reach.</summary>
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

    // Expected values from CPython 3.13: template.format(token_name=value).
    [Theory]
    [InlineData("{{{{ {token_name} }}}}", "name", "{{ name }}")]
    [InlineData("<<{token_name}>>", "name", "<<name>>")]
    [InlineData("{{literal}}", "name", "{literal}")]
    [InlineData("plain", "name", "plain")]
    [InlineData("{token_name!r}", "tok", "'tok'")]
    [InlineData("{token_name!a}", "na\u00efve\U0001F600", "'na\\xefve\\U0001f600'")]
    [InlineData("{token_name!s}", "tok", "tok")]
    [InlineData("{token_name:>12}", "tok", "         tok")]
    [InlineData("{token_name:>12}", "na\u00efve\U0001F600", "      na\u00efve\U0001F600")]
    [InlineData("{token_name[0]}", "tok", "t")]
    [InlineData("{token_name[0]}", "\U0001F600x", "\U0001F600")]
    [InlineData("{token_name:^9}", "na\u00efve\U0001F600", " na\u00efve\U0001F600  ")]
    [InlineData("{token_name:*^10.2}", "tok", "****to****")]
    [InlineData("{token_name:010}", "tok", "tok0000000")]
    [InlineData("{token_name:\u0661\u0662}", "tok", "tok         ")]
    [InlineData("{token_name:\U0001F600^7}", "tok", "\U0001F600\U0001F600tok\U0001F600\U0001F600")]
    [InlineData("{token_name!s:>{token_name[1]}}", "t7", "     t7")]
    [InlineData("{token_name:{{}}}", "tok", null)]
    public void FormatPlaceholderFollowsStrFormat(string template, string value, string? expected)
    {
        var format = () => HuntEngine.FormatPlaceholder(template, value);

        if (expected is null)
        {
            format.Should().Throw<FormatException>().WithMessage("Invalid format specifier '{}' for object of type 'str'");
            return;
        }

        format().Should().Be(expected);
    }

    [Fact]
    public void FormatPlaceholderRejectsOtherFieldsAsPlanTransformDoes()
    {
        var format = () => HuntEngine.FormatPlaceholder("{other}", "name");

        format.Should().Throw<ArgumentException>().WithMessage("placeholder_template must include {token_name} placeholder")
            .WithInnerException<KeyNotFoundException>().WithMessage("'other'");
    }

    // Expected exception types and messages from CPython 3.13 (IndexError, ValueError, TypeError, AttributeError).
    [Theory]
    [InlineData("{}", typeof(StrFormatIndexException), "Replacement index 0 out of range for positional args tuple")]
    [InlineData("{0}", typeof(StrFormatIndexException), "Replacement index 0 out of range for positional args tuple")]
    [InlineData("{\u0660}", typeof(StrFormatIndexException), "Replacement index 0 out of range for positional args tuple")]
    [InlineData("{token_name[9]}", typeof(StrFormatIndexException), "string index out of range")]
    [InlineData("oops }", typeof(FormatException), "Single '}' encountered in format string")]
    [InlineData("oops {", typeof(FormatException), "Single '{' encountered in format string")]
    [InlineData("oops {token_name", typeof(FormatException), "expected '}' before end of string")]
    [InlineData("{token_name:=5}", typeof(FormatException), "'=' alignment not allowed in string format specifier")]
    [InlineData("{token_name:+}", typeof(FormatException), "Sign not allowed in string format specifier")]
    [InlineData("{token_name: }", typeof(FormatException), "Space not allowed in string format specifier")]
    [InlineData("{token_name:z}", typeof(FormatException), "Negative zero coercion (z) not allowed in string format specifier")]
    [InlineData("{token_name:#}", typeof(FormatException), "Alternate form (#) not allowed in string format specifier")]
    [InlineData("{token_name:,}", typeof(FormatException), "Cannot specify ',' with 's'.")]
    [InlineData("{token_name:_s}", typeof(FormatException), "Cannot specify '_' with 's'.")]
    [InlineData("{token_name:,_}", typeof(FormatException), "Cannot specify both ',' and '_'.")]
    [InlineData("{token_name:d}", typeof(FormatException), "Unknown format code 'd' for object of type 'str'")]
    [InlineData("{token_name:ss}", typeof(FormatException), "Invalid format specifier 'ss' for object of type 'str'")]
    [InlineData("{token_name:.}", typeof(FormatException), "Format specifier missing precision")]
    [InlineData("{token_name:{token_name}}", typeof(FormatException), "Invalid format specifier 'name' for object of type 'str'")]
    [InlineData("{token_name!}", typeof(FormatException), "unmatched '{' in format spec")]
    [InlineData("{token_name!x}", typeof(FormatException), "Unknown conversion specifier x")]
    [InlineData("{token_name!rx}", typeof(FormatException), "expected ':' after conversion specifier")]
    [InlineData("{token_name!", typeof(FormatException), "end of string while looking for conversion specifier")]
    [InlineData("{token_name[}", typeof(FormatException), "expected '}' before end of string")]
    [InlineData("{token_name[]}", typeof(FormatException), "Empty attribute in format string")]
    [InlineData("{token_name[0]x}", typeof(FormatException), "Only '.' or '[' may follow ']' in format field specifier")]
    [InlineData("{token_name.}", typeof(FormatException), "Empty attribute in format string")]
    [InlineData("a{token_name:{token_name:{token_name}}}", typeof(FormatException), "Max string recursion exceeded")]
    [InlineData("{tok{en}", typeof(FormatException), "unexpected '{' in field name")]
    [InlineData("{}{0}", typeof(StrFormatIndexException), "Replacement index 0 out of range for positional args tuple")]
    [InlineData("{token_name[x]}", typeof(InvalidCastException), "string indices must be integers, not 'str'")]
    [InlineData("{token_name.x}", typeof(MissingMemberException), "'str' object has no attribute 'x'")]
    [InlineData("{token_name.upper}", typeof(NotSupportedException), "*")]
    public void FormatPlaceholderRaisesLikeStrFormat(string template, Type exceptionType, string message)
    {
        var format = () => HuntEngine.FormatPlaceholder(template, "name");

        var thrown = format.Should().Throw<Exception>().Which;
        thrown.Should().BeOfType(exceptionType);
        thrown.Message.Should().Match(message);
    }

    [Fact]
    public void KeywordsAreLoweredWithStrLowerAndMatchedOnCodePoints()
    {
        var rule = new HuntRule("r", "d", keywords: ["İ", "\U0001F600"]);

        rule.Keywords.Should().Equal("i̇", "\U0001F600");
        HuntEngine.MatchesKeywords("İ \U0001F600", rule.Keywords).Should().BeTrue();
        HuntEngine.MatchesKeywords("i \U0001F600", rule.Keywords).Should().BeFalse();
        HuntEngine.MatchesKeywords("İ\U0001F600", ["\uD83D"]).Should().BeFalse();
    }
}
