using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Registry;

namespace DriftBuster.Backend.Tests.Registry;

/// <summary>
/// The generator's fake backend over <c>Data/registry_cases.json</c> trees: the first key whose hive and path match and whose view is
/// "*" or the requested view answers; values are decoded with <see cref="RegistryOracle.Decode"/>.
/// </summary>
internal sealed class OracleRegistryBackend(object? tree) : IRegistryBackend
{
    private readonly List<OrderedDictionary<string, object?>> _keys = RegistryOracle.Items(tree).Cast<OrderedDictionary<string, object?>>().ToList();

    private OrderedDictionary<string, object?>? Find(string hive, string path, string? view)
        => _keys.FirstOrDefault(key =>
            string.Equals((string)key["hive"]!, hive, StringComparison.Ordinal)
            && string.Equals((string)key["path"]!, path, StringComparison.Ordinal)
            && (key.GetValueOrDefault("view", "*") is "*" || Equals(key.GetValueOrDefault("view", "*"), view)));

    public IReadOnlyList<string> EnumSubkeys(string hive, string path, string? view)
        => Find(hive, path, view) is { } key ? RegistryOracle.Items(key.GetValueOrDefault("subkeys", new List<object?>())).Cast<string>().ToList() : [];

    public IReadOnlyList<KeyValuePair<string, object?>> EnumValues(string hive, string path, string? view)
        => Find(hive, path, view) is { } key
            ? RegistryOracle.Items(key.GetValueOrDefault("values", new List<object?>()))
                .Select(pair => RegistryOracle.Items(pair))
                .Select(pair => new KeyValuePair<string, object?>((string)pair[0]!, RegistryOracle.Decode(pair[1])))
                .ToList()
            : [];
}
