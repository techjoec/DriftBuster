using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Registry;

namespace DriftBuster.Backend.Tests.Registry;

/// <summary>
/// An in-memory registry backend: keys by <c>(hive, path)</c>, whatever the view; adding a key lists it
/// under its parent; subkeys come back sorted, values in insertion order (a later value of the same name keeps the first slot).
/// </summary>
internal sealed class FakeRegistryBackend : IRegistryBackend
{
    private readonly Dictionary<(string, string), (Dictionary<string, bool> Subkeys, OrderedDictionary<string, object?> Values)> _nodes = [];

    public void AddKey(string hive, string path, params (string Name, object? Value)[] values)
    {
        var node = Node(hive, path);
        foreach (var (name, value) in values)
        {
            node.Values[name] = value;
        }

        var split = path.LastIndexOf('\\');
        if (split >= 0)
        {
            Node(hive, path[..split]).Subkeys[path[(split + 1)..]] = true;
        }
    }

    private (Dictionary<string, bool> Subkeys, OrderedDictionary<string, object?> Values) Node(string hive, string path)
    {
        if (!_nodes.TryGetValue((hive, path), out var node))
        {
            node = ([], new OrderedDictionary<string, object?>(StringComparer.Ordinal));
            _nodes[(hive, path)] = node;
        }

        return node;
    }

    public IReadOnlyList<string> EnumSubkeys(string hive, string path, string? view)
    {
        if (!_nodes.TryGetValue((hive, path), out var node))
        {
            return [];
        }

        var names = node.Subkeys.Keys.ToList();
        names.Sort(StringComparer.Ordinal);
        return names;
    }

    public IReadOnlyList<KeyValuePair<string, object?>> EnumValues(string hive, string path, string? view)
        => _nodes.TryGetValue((hive, path), out var node) ? node.Values.ToList() : [];
}
