using DriftBuster.Backend.Models;
using DriftBuster.Backend.Secrets;

namespace DriftBuster.Backend.Tests.Secrets;

/// <summary>The packaged ruleset, ruleset parsing and the detection context a run builds.</summary>
public sealed class SecretRulesTests
{
    [Fact]
    public void The_packaged_rules_load_once_with_their_version()
    {
        var rules = SecretRules.Packaged;

        rules.Version.Should().Be("2024-06-01");
        rules.Rules.Select(rule => rule.Name).Should().Contain("PasswordAssignment");
        SecretRules.Packaged.Should().BeSameAs(rules);
    }

    [Fact]
    public void A_flag_i_rule_ignores_case()
    {
        var rules = SecretRules.Parse("""{"version": "v", "rules": [{"name": "Token", "description": null, "pattern": "secret", "flags": "i"}]}""");

        rules.Rules.Should().ContainSingle().Which.Pattern.IsMatch("SECRET").Should().BeTrue();
    }

    [Theory]
    [InlineData("""{"version": "v", "rules": [{"name": "x", "pattern": "(", "description": null}]}""", "Secret rule 'x': *")]
    [InlineData("""{"version": "v", "rules": [], "extra": 1}""", "Secret rules: $.extra: *")]
    [InlineData("""{"rules": []}""", "Secret rules: $: *version*")]
    [InlineData("""{not json""", "Secret rules: $: *")]
    public void A_ruleset_that_does_not_hold_is_refused(string json, string message)
        => FluentActions.Invoking(() => SecretRules.Parse(json)).Should().Throw<InvalidDataException>().WithMessage(message);

    [Fact]
    public void The_context_carries_the_profile_ignore_lists()
    {
        var context = SecretScanner.BuildContext(new SecretScannerOptions { IgnoreRules = ["Token"], IgnorePatterns = ["ALLOW", "ALLOW", "("] });

        context.RulesLoaded.Should().BeTrue();
        context.IgnoreRules.Should().BeEquivalentTo(["Token"]);
        context.IgnorePatternText.Should().Equal("ALLOW", "(");
        context.IgnorePatterns.Should().ContainSingle();
    }
}
