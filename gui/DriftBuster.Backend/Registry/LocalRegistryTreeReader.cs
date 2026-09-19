using System.Runtime.Versioning;

namespace DriftBuster.Backend.Registry;

/// <summary>Reads the local registry (Windows only) through <see cref="WinRegistryBackend"/>, keeping each value's type and bytes.</summary>
[SupportedOSPlatform("windows")]
public sealed class LocalRegistryTreeReader : IRegistryTreeReader
{
    public RegistryTreeRead Read(IReadOnlyList<RegistryRoot> roots, int maxDepth, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(roots);
        var backend = new WinRegistryBackend();
        return RegistryTreeWalk.Walk(
            roots,
            maxDepth,
            (root) => WinRegistryBackend.KeyExists(root.Hive, root.Path, root.View)
                ? new RegistryTreeNode(root.Hive, root.Path, root.View, backend.EnumSubkeys(root.Hive, root.Path, root.View), WinRegistryBackend.EnumRawValues(root.Hive, root.Path, root.View))
                : null,
            cancellationToken);
    }
}
