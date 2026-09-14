namespace DriftBuster.Backend.Tests.Infrastructure;

/// <summary>
/// Test classes that bound elapsed wall-clock time run on their own, after the parallel collections: under coverage
/// instrumentation, CPU-heavy classes running beside them stretch the elapsed time of a linear pass past its bound.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WallClockCollection
{
    public const string Name = "wall-clock";
}
