using DriftBuster.Backend.Infrastructure.EngineRe;
using DriftBuster.Backend.Secrets;

namespace DriftBuster.Backend.Tests.Secrets;

/// <summary>
/// The offline runner's secret-scanning helpers: ruleset compilation from a mapping, secret option values, the manifest scanner
/// block and the secret context. The runner execution tests are Pester tests of the offline runner script.
/// </summary>
[Collection(SecretRuleCacheCollection.Name)]
public sealed class OfflineRunnerTests : IDisposable
{
    private readonly SecretRuleCacheIsolation _isolation = new();

    public void Dispose() => _isolation.Dispose();

    private static OrderedDictionary<string, object?> Map(params (string Key, object? Value)[] items)
    {
        var map = new OrderedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in items)
        {
            map[key] = value;
        }

        return map;
    }

    [Fact]
    public void CompileRulesetFromMappingHandlesInvalidEntries()
    {
        SecretScanner.CompileRulesetFromMapping(null).Should().BeNull();
        SecretScanner.CompileRulesetFromMapping(Map(("rules", "invalid"))).Should().BeNull();

        var payload = Map(
            ("version", "custom"),
            ("rules", new List<object?>
            {
                Map(("name", "Valid"), ("pattern", "secret"), ("flags", "i")),
                Map(("name", "Broken"), ("pattern", "[")),
            }));
        var compiled = SecretScanner.CompileRulesetFromMapping(payload);
        compiled.Should().NotBeNull();
        compiled!.Version.Should().Be("custom");
        compiled.Rules.Should().ContainSingle();
        compiled.Rules[0].Should().BeOfType<SecretDetectionRule>();
    }

    [Fact]
    public void SecretOptionValuesAndManifestHelpers()
    {
        SecretScanner.SecretOptionValues("a, b ; c").Should().Equal("a", "b", "c");
        SecretScanner.SecretOptionValues(new List<object?> { "x", null, " y " }).Should().Equal("x", "y");

        var context = new SecretDetectionContext(
            rules: [],
            version: "v1",
            ignoreRules: new HashSet<string>(StringComparer.Ordinal) { "Skip" },
            ignorePatterns: [EnginePattern.Compile("SKIP")],
            ignorePatternText: ["SKIP"],
            rulesLoaded: true);
        var manifest = SecretScanner.ManifestSecretScanner(
            Map(("secret_ignore_rules", "Skip")),
            Map(("ignore_patterns", new List<object?> { "SKIP" })),
            context);
        manifest["ruleset_version"].Should().Be("v1");
        ((List<object?>)manifest["ignore_rules"]!).Should().Equal("Skip");
        ((List<object?>)manifest["ignore_patterns"]!).Should().Equal("SKIP");
    }

    [Fact]
    public void BuildSecretContextPrefersInlineRules()
    {
        var payload = Map(
            ("ruleset", Map(("version", "inline"), ("rules", new List<object?> { Map(("name", "Token"), ("pattern", "VALUE")) }))),
            ("ignore_rules", new List<object?> { "Token" }));
        var context = SecretScanner.BuildContext(Map(("secret_ignore_patterns", new List<object?> { "ALLOW" })), payload);

        context.Version.Should().Be("inline");
        context.RulesLoaded.Should().BeTrue();
        context.IgnoreRules.Should().BeEquivalentTo(["Token"]);
        context.IgnorePatternText.Should().Contain("ALLOW");
    }
}
