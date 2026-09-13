namespace DriftBuster.Backend.Tests.Hunt;

/// <summary>
/// Test classes that run hunts share the process-wide <c>HuntEngine.RelativeTo</c> seam, which two tests swap as the
/// Python tests monkeypatch <c>Path.relative_to</c> and <c>Path.glob</c>; the collection keeps them from running concurrently.
/// </summary>
[CollectionDefinition(Name)]
public sealed class HuntSeamCollection
{
    public const string Name = "hunt-seams";
}
