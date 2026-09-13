using DriftBuster.Backend.Diff;

namespace DriftBuster.Backend.Tests.Diff;

/// <summary>Mirror of tests/reporting/test_redaction_utils.py.</summary>
public sealed class RedactionUtilsTests
{
    [Fact]
    public void RedactionFilterAppliesAndTracksCounts()
    {
        var redactor = new RedactionFilter(["token", "tokenised", "secret"], placeholder: "***");

        // Ensure tokens are ordered by length so the longest match wins first.
        redactor.OrderedTokens[0].Should().Be("tokenised");

        var text = "tokenised value with token and secret";
        var redacted = redactor.Apply(text);

        redacted.Should().Be("*** value with *** and ***");
        redactor.HasHits.Should().BeTrue();
        redactor.Stats().Should().BeEquivalentTo(new Dictionary<string, int>(StringComparer.Ordinal) { ["tokenised"] = 1, ["token"] = 1, ["secret"] = 1 });

        redactor.Reset();
        redactor.HasHits.Should().BeFalse();
        redactor.Apply(string.Empty).Should().BeEmpty();
    }

    [Fact]
    public void RedactDataHandlesNestedStructures()
    {
        var redactor = new RedactionFilter(["secret"]);

        static IEnumerable<string> Generator()
        {
            yield return "value";
            yield return "secret";
        }

        var payload = new OrderedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["message"] = "this is secret",
            ["list"] = new List<object?> { "secret", new OrderedDictionary<string, object?>(StringComparer.Ordinal) { ["nested"] = "no secret" } },
            ["tuple"] = new object?[] { "keep", "secret" },
            ["set"] = new HashSet<object?> { "secret", "visible" },
        };
        payload["iterable"] = Generator();

        var redacted = DiffOracleData.Map(RedactionFilter.RedactData(payload, redactor));

        ((string)redacted["message"]!).Should().EndWith("[REDACTED]");
        var list = DiffOracleData.List(redacted["list"]);
        list[0].Should().Be("[REDACTED]");
        DiffOracleData.Map(list[1])["nested"].Should().Be("no [REDACTED]");
        ((object?[])redacted["tuple"]!)[1].Should().Be("[REDACTED]");
        ((HashSet<object?>)redacted["set"]!).Should().Contain("[REDACTED]");
        DiffOracleData.List(redacted["iterable"])[1].Should().Be("[REDACTED]");
    }

    [Fact]
    public void RedactDataFollowsRuntimeCategoriesForTypedCollections()
    {
        var redactor = new RedactionFilter(["secret"]);

        RedactionFilter.RedactData(new HashSet<string>(StringComparer.Ordinal) { "secret", "plain" }, redactor)
            .Should().BeOfType<HashSet<object?>>().Which.Should().BeEquivalentTo(new object?[] { "[REDACTED]", "plain" });
        RedactionFilter.RedactData(new SortedSet<int> { 2, 1 }, redactor).Should().BeOfType<HashSet<object?>>();
        RedactionFilter.RedactData(new[] { "secret", "x" }, redactor).Should().BeOfType<object?[]>().Which.Should().Equal("[REDACTED]", "x");
        RedactionFilter.RedactData(new[] { 1, 2 }, redactor).Should().BeOfType<object?[]>().Which.Should().Equal(1, 2);
        RedactionFilter.RedactData(new List<string> { "secret" }, redactor).Should().BeOfType<List<object?>>().Which.Should().Equal("[REDACTED]");
        RedactionFilter.RedactData(new Dictionary<int, string> { [1] = "secret" }, redactor)
            .Should().BeOfType<OrderedDictionary<string, object?>>().Which["1"].Should().Be("[REDACTED]");
        byte[] bytes = [1, 2];
        RedactionFilter.RedactData(bytes, redactor).Should().BeSameAs(bytes);
        RedactionFilter.RedactData(5, redactor).Should().Be(5);
    }

    [Fact]
    public void ResolveRedactorVariants()
    {
        var existing = new RedactionFilter(["keep"]);
        var resolved = RedactionFilter.Resolve(redactor: existing);
        resolved.Should().BeSameAs(existing);

        var auto = RedactionFilter.Resolve(maskTokens: ["x", "y"], placeholder: "?");
        auto.Should().BeOfType<RedactionFilter>();
        auto!.Placeholder.Should().Be("?");

        var act = () => RedactionFilter.Resolve(redactor: existing, maskTokens: ["oops"]);
        act.Should().Throw<ArgumentException>();
    }
}
