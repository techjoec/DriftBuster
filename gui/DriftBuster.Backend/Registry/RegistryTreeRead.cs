namespace DriftBuster.Backend.Registry;

/// <summary>The keys read under a set of roots; <see cref="Truncated"/> when the key or time limit stopped the read.</summary>
public sealed record RegistryTreeRead(IReadOnlyList<RegistryTreeNode> Nodes, bool Truncated);
