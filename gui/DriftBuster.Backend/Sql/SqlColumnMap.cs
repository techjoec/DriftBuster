using System.Collections;

using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Sql;

/// <summary>
/// The column map parser and its two readers: which columns of which table a snapshot masks or hashes.
/// Inputs follow the values <see cref="EngineJson"/> produces (dict, list, str, int, float, bool, <c>None</c>); a string array or list
/// is a Python list, and a dictionary of string lists is a mapping.
/// </summary>
public static class SqlColumnMap
{
    /// <summary>
    /// <c>parse_column_map(values)</c>: a mapping goes through <see cref="NormaliseColumnMap"/>, anything else through
    /// <see cref="ParseColumnList"/>. Tables keep their first-seen order.
    /// </summary>
    /// <exception cref="InvalidDataException">A list entry that is truthy and not a str, or a truthy value that is neither a mapping nor
    /// iterable.</exception>
    public static OrderedDictionary<string, IReadOnlyList<string>> ParseColumnMap(object? values) => values switch
    {
        IReadOnlyDictionary<string, object?> mapping => NormaliseColumnMap(mapping),
        IReadOnlyDictionary<string, IReadOnlyList<string>> typed => NormaliseColumnMap(
            Ordered(typed.Select(item => new KeyValuePair<string, object?>(item.Key, item.Value?.Cast<object?>().ToList())))),
        _ => ParseColumnList(values),
    };

    /// <summary>
    /// <c>_normalise_column_map(values)</c>: an empty mapping gives no tables; a table with an empty name is skipped; a sequence of
    /// columns (a list, or a str, whose code points are its items) keeps <c>str(column)</c> of each item whose <c>str.strip()</c> is not
    /// empty, unstripped; any other value becomes the single column <c>str(value)</c>.
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
    /// <c>_parse_column_list(values)</c>: each truthy entry is stripped and split at its first "." into a table and a column, both
    /// stripped; an entry without a dot, or with an empty table or column, is skipped. A str is iterated by code point, so it never
    /// names a column.
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
