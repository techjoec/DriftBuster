using DriftBuster.Backend.Secrets;

namespace DriftBuster.Backend.Tests.Secrets;

/// <summary>The autouse <c>_isolate_secret_rule_caches</c> fixture: restores the cache to its state before the test.</summary>
public sealed class SecretRuleCacheIsolation : IDisposable
{
    private readonly IReadOnlyList<SecretDetectionRule>? _rules = SecretScanner.RuleCache;
    private readonly string? _version = SecretScanner.RuleVersion;
    private readonly bool? _loaded = SecretScanner.RuleLoaded;
    private readonly Func<string?> _reader = SecretScanner.ResourceReader;

    public void Dispose()
    {
        (SecretScanner.RuleCache, SecretScanner.RuleVersion, SecretScanner.RuleLoaded) = (_rules, _version, _loaded);
        SecretScanner.ResourceReader = _reader;
    }
}
