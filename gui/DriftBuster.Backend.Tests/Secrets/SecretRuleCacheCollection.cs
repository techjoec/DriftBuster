namespace DriftBuster.Backend.Tests.Secrets;

/// <summary>
/// Test classes that load secret rules share the process-wide rule cache, which tests/conftest.py snapshots and restores
/// around every test; the collection keeps them from running concurrently and <see cref="SecretRuleCacheIsolation"/>
/// does the snapshot and restore.
/// </summary>
[CollectionDefinition(Name)]
public sealed class SecretRuleCacheCollection
{
    public const string Name = "secret-rule-cache";
}
