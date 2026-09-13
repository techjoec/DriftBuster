using DriftBuster.Backend.Infrastructure.PythonRe;
using DriftBuster.Backend.Secrets;

namespace DriftBuster.Backend.Tests.Secrets;

/// <summary>Mirror of tests/secret_scanning/test_rule_cache.py.</summary>
[Collection(SecretRuleCacheCollection.Name)]
public sealed class RuleCacheTests : IDisposable
{
    private readonly SecretRuleCacheIsolation _isolation = new();

    public void Dispose() => _isolation.Dispose();

    [Fact]
    public void LoadSecretRulesReadsPackagedRulesAndCaches()
    {
        SecretScanner.ResetSecretRuleCache();

        var (rules, version, loaded) = SecretScanner.LoadSecretRules();
        loaded.Should().BeTrue();
        rules.Should().NotBeEmpty("expected packaged secret rules to be available");
        version.Should().NotBeNullOrEmpty().And.NotBe("none");

        IReadOnlyList<SecretDetectionRule> sentinelRules = [new SecretDetectionRule("sentinel", PythonPattern.Compile("sentinel"))];
        SecretScanner.RuleCache = sentinelRules;
        SecretScanner.RuleVersion = "cache-version";
        SecretScanner.RuleLoaded = true;
        var (cachedRules, cachedVersion, cachedLoaded) = SecretScanner.LoadSecretRules();
        cachedRules.Should().BeSameAs(sentinelRules);
        cachedVersion.Should().Be("cache-version");
        cachedLoaded.Should().BeTrue();

        SecretScanner.ResetSecretRuleCache();
        SecretScanner.RuleCache.Should().BeNull();
        SecretScanner.RuleVersion.Should().BeNull();
        SecretScanner.RuleLoaded.Should().BeNull();
    }
}
