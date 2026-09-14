namespace DriftBuster.Backend.Tests.MultiServer;

/// <summary>Tests that set the process-wide <c>DRIFTBUSTER_DATA_ROOT</c> variable run alone, after the parallel collections.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DataRootEnvironmentCollection
{
    public const string Name = "data-root-environment";
}
