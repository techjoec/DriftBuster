namespace DriftBuster.Backend.Tests.Infrastructure;

/// <summary>Test classes that set a process-wide seam every other test reads run alone, after the parallel collections.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessWideSeamCollection
{
    public const string Name = "process-wide-seam";
}
