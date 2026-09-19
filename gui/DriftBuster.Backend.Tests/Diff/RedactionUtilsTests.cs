using System.Text.Json.Nodes;

using DriftBuster.Backend.Diff;

namespace DriftBuster.Backend.Tests.Diff;

/// <summary>The redaction helpers.</summary>
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
    public void Every_string_value_in_a_json_tree_is_redacted_in_place()
    {
        var redactor = new RedactionFilter(["secret"]);
        var payload = new JsonObject
        {
            ["message"] = "this is secret",
            ["list"] = new JsonArray("secret", new JsonObject { ["nested"] = "no secret" }, 3),
            ["secret"] = true,
        };

        redactor.ApplyTo(payload).Should().BeSameAs(payload);

        payload.ShouldBeJson(new JsonObject
        {
            ["message"] = "this is [REDACTED]",
            ["list"] = new JsonArray("[REDACTED]", new JsonObject { ["nested"] = "no [REDACTED]" }, 3),
            ["secret"] = true,
        });
        redactor.ApplyTo(JsonValue.Create("secret")).ShouldBeJson("[REDACTED]");
        redactor.ApplyTo(null).Should().BeNull();
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
