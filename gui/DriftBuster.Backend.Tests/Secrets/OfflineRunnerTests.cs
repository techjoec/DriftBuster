using DriftBuster.Backend.Models;
using DriftBuster.Backend.Secrets;

namespace DriftBuster.Backend.Tests.Secrets;

/// <summary>Ruleset compilation from a mapping and the secret context a run builds.</summary>
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
    public void The_context_carries_the_profile_ignore_lists()
    {
        var context = SecretScanner.BuildContext(new SecretScannerOptions { IgnoreRules = ["Token"], IgnorePatterns = ["ALLOW", "ALLOW", "("] });

        context.RulesLoaded.Should().BeTrue();
        context.IgnoreRules.Should().BeEquivalentTo(["Token"]);
        context.IgnorePatternText.Should().Equal("ALLOW", "(");
        context.IgnorePatterns.Should().ContainSingle();
    }
}
