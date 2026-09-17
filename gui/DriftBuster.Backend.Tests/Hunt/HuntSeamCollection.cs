namespace DriftBuster.Backend.Tests.Hunt;

/// <summary>
/// Test classes that run hunts share the process-wide <c>HuntEngine</c> seams; the collection keeps them from running concurrently.
/// </summary>
[CollectionDefinition(Name)]
public sealed class HuntSeamCollection
{
    public const string Name = "hunt-seams";
}
