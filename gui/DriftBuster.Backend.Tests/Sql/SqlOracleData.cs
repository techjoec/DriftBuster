using System.Globalization;

using DriftBuster.Backend.Diff;
using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Sql;

namespace DriftBuster.Backend.Tests.Sql;

/// <summary>
/// CPython oracle data written by <c>tools/parity/gen_sql_cases.py</c> (<c>Data/sql_cases.json</c>), read with <see cref="PythonJson"/>. A
/// float travels as <c>{"$float": repr}</c> and bytes as <c>{"$bytes": hex}</c>, so both survive JSON exactly.
/// </summary>
internal static class SqlOracleData
{
    private static readonly Lazy<OrderedDictionary<string, object?>> Data = new(() =>
    {
        var path = Path.Combine(RepoPaths.Root, "gui", "DriftBuster.Backend.Tests", "Sql", "Data", "sql_cases.json");
        return PythonJson.TryLoads(File.ReadAllText(path), out var value)
            ? (OrderedDictionary<string, object?>)value!
            : throw new InvalidDataException($"{path} is not valid JSON");
    });

    public static IEnumerable<OrderedDictionary<string, object?>> Cases(string section)
        => ((List<object?>)Data.Value[section]!).Cast<OrderedDictionary<string, object?>>();

    /// <summary>The cases of <paramref name="section"/> as theory rows keyed by their index.</summary>
    public static TheoryData<int> Indexes(string section)
    {
        var data = new TheoryData<int>();
        foreach (var index in Enumerable.Range(0, Cases(section).Count()))
        {
            data.Add(index);
        }

        return data;
    }

    public static OrderedDictionary<string, object?> Case(string section, int index) => Cases(section).ElementAt(index);

    /// <summary>A section that holds one case rather than a list.</summary>
    public static object? Case(string section) => Data.Value[section];

    /// <summary>Turns the oracle's tagged floats and bytes back into <see cref="double"/> and <see cref="byte"/> arrays.</summary>
    public static object? Decode(object? value) => value switch
    {
        OrderedDictionary<string, object?> { Count: 1 } tagged when tagged.TryGetValue("$float", out var repr) => ParseFloat((string)repr!),
        OrderedDictionary<string, object?> { Count: 1 } tagged when tagged.TryGetValue("$bytes", out var hex) => Convert.FromHexString((string)hex!),
        OrderedDictionary<string, object?> map => Ordered(map.Select(item => (item.Key, Decode(item.Value)))),
        List<object?> list => list.Select(Decode).ToList(),
        _ => value,
    };

    /// <summary>The oracle's tagged form of a value: floats as <c>{"$float": repr}</c>, bytes as <c>{"$bytes": hex}</c>.</summary>
    public static object? Encode(object? value) => value switch
    {
        double number => Ordered(("$float", PythonRepr.Float(number))),
        byte[] bytes => Ordered(("$bytes", Convert.ToHexStringLower(bytes))),
        OrderedDictionary<string, object?> map => Ordered(map.Select(item => (item.Key, Encode(item.Value)))),
        List<object?> list => list.Select(Encode).ToList(),
        _ => value,
    };

    /// <summary>A one-line, key-order-preserving JSON text for comparing oracle values.</summary>
    public static string Text(object? value) => Canonicaliser.Dumps(value, indent: false, ensureAscii: true, sortKeys: false);

    public static OrderedDictionary<string, object?> Ordered(params IEnumerable<(string Key, object? Value)> items)
    {
        var map = new OrderedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in items)
        {
            map[key] = value;
        }

        return map;
    }

    /// <summary>
    /// Null when <paramref name="exc"/> is the oracle's <c>{"type", "message"}</c> error (the message with <c>{dir}</c> replaced by
    /// <paramref name="directory"/>); otherwise the difference.
    /// </summary>
    public static string? ErrorMismatch(Exception exc, object? expected, string? directory = null)
    {
        var error = (OrderedDictionary<string, object?>)expected!;
        var type = (string)error["type"]!;
        var message = (string)error["message"]!;
        if (directory is not null)
        {
            message = message.Replace("{dir}", directory, StringComparison.Ordinal);
        }

        var typeMatches = exc switch
        {
            Sqlite3Exception sqlite => string.Equals(sqlite.TypeName, type, StringComparison.Ordinal),
            PythonValueException => type is "ValueError",
            PythonAttributeException => type is "AttributeError",
            PythonTypeException => type is "TypeError",
            PythonIndexException => type is "IndexError",
            FileNotFoundException => type is "FileNotFoundError",
            _ => false,
        };
        return typeMatches && string.Equals(exc.Message, message, StringComparison.Ordinal)
            ? null
            : $"raised {exc.GetType().Name} ({(exc as Sqlite3Exception)?.TypeName}): {exc.Message}; expected {type}: {message}";
    }

    private static double ParseFloat(string repr) => repr switch
    {
        "inf" => double.PositiveInfinity,
        "-inf" => double.NegativeInfinity,
        "nan" => double.NaN,
        _ => double.Parse(repr, NumberStyles.Float, CultureInfo.InvariantCulture),
    };
}
