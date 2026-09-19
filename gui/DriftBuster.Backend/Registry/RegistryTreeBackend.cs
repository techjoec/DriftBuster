namespace DriftBuster.Backend.Registry;

/// <summary>An <see cref="IRegistryBackend"/> over keys already read, so app discovery runs against a remote host's snapshot.</summary>
internal sealed class RegistryTreeBackend(IReadOnlyList<RegistryTreeNode> nodes) : IRegistryBackend
{
    private readonly Dictionary<(string, string, string?), RegistryTreeNode> _nodes = nodes
        .GroupBy(node => (node.Hive, node.Path.ToUpperInvariant(), node.View))
        .ToDictionary(group => group.Key, group => group.First());

    public IReadOnlyList<string> EnumSubkeys(string hive, string path, string? view) =>
        _nodes.TryGetValue((hive, path.ToUpperInvariant(), view), out var node) ? node.Subkeys : [];

    public IReadOnlyList<KeyValuePair<string, object?>> EnumValues(string hive, string path, string? view) =>
        _nodes.TryGetValue((hive, path.ToUpperInvariant(), view), out var node)
            ? node.Values.Select(value => new KeyValuePair<string, object?>(value.Name, RegistryValueDecoder.Convert(value.Data, value.Type))).ToList()
            : [];
}
