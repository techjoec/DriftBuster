using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Registry;

namespace DriftBuster.Backend.Tests.Registry;

/// <summary>Reads <c>Data/registry_cases.json</c>, written by <c>tools/parity/gen_registry_cases.py</c> from CPython 3.13.</summary>
internal static class RegistryOracle
{
    private static readonly Lazy<OrderedDictionary<string, object?>> Cases = new(() =>
    {
        var path = Path.Combine(RepoPaths.Root, "gui", "DriftBuster.Backend.Tests", "Registry", "Data", "registry_cases.json");
        PythonJson.TryLoads(File.ReadAllText(path), out var value).Should().BeTrue();
        return (OrderedDictionary<string, object?>)value!;
    });

    public static object? Section(string name) => Cases.Value[name];

    public static List<object?> Items(object? value) => (List<object?>)value!;

    public static OrderedDictionary<string, object?> Map(object? value) => (OrderedDictionary<string, object?>)value!;

    /// <summary>The generator's encoding back to values: <c>{"$bytes": hex}</c> is a byte array, containers are walked.</summary>
    public static object? Decode(object? value) => value switch
    {
        OrderedDictionary<string, object?> { Count: 1 } map when map.TryGetValue("$bytes", out var hex) => Convert.FromHexString((string)hex!),
        OrderedDictionary<string, object?> map => new OrderedDictionary<string, object?>(
            map.Select(pair => new KeyValuePair<string, object?>(pair.Key, Decode(pair.Value))), StringComparer.Ordinal),
        List<object?> list => list.Select(Decode).ToList(),
        _ => value,
    };

    public static RegistryApp App(object? value)
    {
        var map = Map(value);
        return new RegistryApp(
            (string)map["display_name"]!,
            (string)map["key_path"]!,
            (string)map["hive"]!,
            (string?)map.GetValueOrDefault("publisher"),
            (string?)map.GetValueOrDefault("version"),
            (string?)map.GetValueOrDefault("uninstall_string"),
            (string?)map.GetValueOrDefault("install_location"),
            (string?)map.GetValueOrDefault("view") ?? "auto");
    }

    public static RegistryRoot Root(object? value)
    {
        var items = Items(value);
        return new RegistryRoot((string)items[0]!, (string)items[1]!, (string?)items[2]);
    }

    public static RegistryHit Hit(object? value)
    {
        var map = Map(value);
        return new RegistryHit((string)map["path"]!, (string)map["hive"]!, (string)map["value_name"]!, (string)map["data_preview"]!, (string)map["reason"]!);
    }

    /// <summary>A C# failure as the generator records a Python one.</summary>
    public static OrderedDictionary<string, object?> Error(Exception exc) => new(StringComparer.Ordinal)
    {
        ["type"] = RegistryPython.ErrorName(exc),
        ["message"] = exc.Message,
    };
}
