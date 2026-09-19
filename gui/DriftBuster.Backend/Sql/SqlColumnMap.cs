using System.Collections;

using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Sql;

/// <summary>
/// Which columns of which table a snapshot masks or hashes. Inputs are <see cref="EngineJson"/> values: a string array or list is a
/// list, a dictionary of string lists is a mapping.
/// </summary>
public static class SqlColumnMap
{
    /// <summary>
    /// A mapping goes through <see cref="NormaliseColumnMap"/>, anything else through <see cref="ParseColumnList"/>. Tables keep their
    /// first-seen order.
    /// </summary>
    public static OrderedDictionary<string, IReadOnlyList<string>> ParseColumnMap(object? values) => values switch
    {
        IReadOnlyDictionary<string, object?> mapping => NormaliseColumnMap(mapping),
        IReadOnlyDictionary<string, IReadOnlyList<string>> typed => NormaliseColumnMap(
            Ordered(typed.Select(item => new KeyValuePair<string, object?>(item.Key, item.Value?.Cast<object?>().ToList())))),
        _ => ParseColumnList(values),
    };

    /// <summary>
    /// An empty mapping gives no tables; a table with an empty name is skipped; a sequence of columns (a list, or a string, whose code
    /// points are its items) keeps each item's text whose trimmed form is not empty, untrimmed; any other value becomes one column, its text.
    /// </summary>
    internal static OrderedDictionary<string, IReadOnlyList<string>> NormaliseColumnMap(IReadOnlyDictionary<string, object?> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var normalised = new OrderedDictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var (table, columns) in values)
        {
            if (string.IsNullOrEmpty(table))
            {
                continue;
            }

            normalised[table] = columns is string or (IList and not IDictionary)
                ? EngineBuiltins.Iterate(columns).Select(EngineRepr.Str).Where(column => EngineText.Strip(column).Length > 0).ToList()
                : [EngineRepr.Str(columns)];
        }

        return normalised;
    }

    /// <summary>
    /// Each truthy entry is stripped and split at its first "." into a table and a column, both stripped; an entry without a dot, or with
    /// an empty table or column, is skipped. A string is iterated by code point, so it never names a column.
    /// </summary>
    internal static OrderedDictionary<string, IReadOnlyList<string>> ParseColumnList(object? values)
    {
        var grouped = new OrderedDictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        if (!EngineBuiltins.IsTruthy(values))
        {
            return grouped;
        }

        var entries = values is IEnumerable<string> sequence and not IList ? sequence.Cast<object?>().ToList() : values;
        foreach (var entry in EngineBuiltins.Iterate(entries))
        {
            if (!EngineBuiltins.IsTruthy(entry))
            {
                continue;
            }

            if (entry is not string raw)
            {
                throw new InvalidDataException($"expected a column name string, not '{EngineBuiltins.TypeName(entry)}'");
            }

            var text = EngineText.Strip(raw);
            var dot = text.IndexOf('.', StringComparison.Ordinal);
            if (dot < 0)
            {
                continue;
            }

            var table = EngineText.Strip(text[..dot]);
            var column = EngineText.Strip(text[(dot + 1)..]);
            if (table.Length == 0 || column.Length == 0)
            {
                continue;
            }

            if (!grouped.TryGetValue(table, out var existing))
            {
                existing = new List<string>();
                grouped[table] = existing;
            }

            ((List<string>)existing).Add(column);
        }

        return grouped;
    }

    private static OrderedDictionary<string, object?> Ordered(IEnumerable<KeyValuePair<string, object?>> items)
    {
        var mapping = new OrderedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in items)
        {
            mapping[key] = value;
        }

        return mapping;
    }
}
