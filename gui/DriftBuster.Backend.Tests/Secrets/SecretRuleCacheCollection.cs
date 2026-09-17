namespace DriftBuster.Backend.Tests.Secrets;

/// <summary>
/// Test classes that load secret rules share the process-wide rule cache; the collection keeps them from running concurrently and
/// <see cref="SecretRuleCacheIsolation"/> snapshots and restores the cache around every test.
/// </summary>
[CollectionDefinition(Name)]
public sealed class SecretRuleCacheCollection
{
    public const string Name = "secret-rule-cache";
}
