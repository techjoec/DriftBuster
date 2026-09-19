using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Registry;

namespace DriftBuster.Backend.Tests.Registry;

/// <summary>
/// Test classes that swap a registry seam (<see cref="RegistryScan.IsWindowsProbe"/>, the <see cref="RegistryCommands"/> delegates)
/// run one at a time.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RegistrySeamCollection
{
    public const string Name = "registry-seams";
}
