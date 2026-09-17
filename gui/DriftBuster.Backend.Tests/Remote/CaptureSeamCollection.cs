namespace DriftBuster.Backend.Tests.Remote;

/// <summary>
/// Test classes that swap the capture runner's process-wide seams (<see cref="CaptureSeams"/>) run one at a time, after the parallel
/// collections.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class CaptureSeamCollection
{
    public const string Name = "capture-seams";
}
