namespace DriftBuster.Backend.Registry;

/// <summary>Reads keys under roots breadth first, each key once, within a depth, key count and time budget.</summary>
public interface IRegistryTreeReader
{
    RegistryTreeRead Read(IReadOnlyList<RegistryRoot> roots, int maxDepth, CancellationToken cancellationToken);
}
