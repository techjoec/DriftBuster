namespace DriftBuster.Backend.Tests.Infrastructure;

/// <summary>Test classes that swap the process-wide <c>PythonOsPath.GetEnvironmentVariable</c> seam run one at a time.</summary>
[CollectionDefinition(Name)]
public sealed class PythonOsPathSeamCollection
{
    public const string Name = "python-os-path-seam";
}
