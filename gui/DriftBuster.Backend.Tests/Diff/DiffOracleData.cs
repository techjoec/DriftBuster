using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Tests.Diff;

/// <summary>
/// CPython oracle data written by <c>tools/parity/gen_diff_cases.py</c>, read with <see cref="PythonJson"/> so unpaired
/// surrogate escapes decode exactly as Python wrote them.
/// </summary>
internal static class DiffOracleData
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, List<OrderedDictionary<string, object?>>> Cache =
        new(StringComparer.Ordinal);

    public static List<OrderedDictionary<string, object?>> Load(string fileName) => Cache.GetOrAdd(fileName, static name =>
    {
        var path = Path.Combine(RepoPaths.Root, "gui", "DriftBuster.Backend.Tests", "Diff", "Data", name);
        if (!PythonJson.TryLoads(File.ReadAllText(path), out var value))
        {
            throw new InvalidDataException($"{path} is not valid JSON");
        }

        return ((List<object?>)value!).Cast<OrderedDictionary<string, object?>>().ToList();
    });

    public static OrderedDictionary<string, object?> Map(object? value) => (OrderedDictionary<string, object?>)value!;

    public static List<object?> List(object? value) => (List<object?>)value!;

    public static List<string> Strings(object? value) => List(value).Cast<string>().ToList();

    public static int Int(object? value) => (int)value!;

    public static TheoryData<string> Names(string fileName)
    {
        var names = new TheoryData<string>();
        foreach (var entry in Load(fileName))
        {
            names.Add((string)(entry.TryGetValue("name", out var name) ? name : Map(entry["case"])["name"])!);
        }

        return names;
    }

    public static OrderedDictionary<string, object?> Case(string fileName, string name)
        => Load(fileName).Single(entry => string.Equals(
            (string)(entry.TryGetValue("name", out var value) ? value : Map(entry["case"])["name"])!,
            name,
            StringComparison.Ordinal));
}
