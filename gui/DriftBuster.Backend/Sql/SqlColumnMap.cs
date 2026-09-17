using System.Collections;

using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Sql;

/// <summary>
/// <c>driftbuster.sql.snapshots.parse_column_map</c> and its two readers: which columns of which table a snapshot masks or hashes.
/// Inputs follow the values <see cref="PythonJson"/> produces (dict, list, str, int, float, bool, <c>None</c>); a string array or list
/// is a Python list, and a dictionary of string lists is a mapping.
/// </summary>
public static class SqlColumnMap
{
    /// <summary>
    /// <c>parse_column_map(values)</c>: a mapping goes through <see cref="NormaliseColumnMap"/>, anything else through
    /// <see cref="ParseColumnList"/>. Tables keep their first-seen order.
    /// </summary>
    /// <exception cref="PythonAttributeException">A list entry that is truthy and not a str (<c>'int' object has no attribute 'strip'</c>).</exception>
    /// <exception cref="PythonTypeException">A truthy value that is neither a mapping nor iterable (<c>'int' object is not iterable</c>).</exception>
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
                ? PythonBuiltins.Iterate(columns).Select(PythonRepr.Str).Where(column => PythonText.Strip(column).Length > 0).ToList()
                : [PythonRepr.Str(columns)];
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
        if (!PythonBuiltins.IsTruthy(values))
        {
            return grouped;
        }

        var entries = values is IEnumerable<string> sequence and not IList ? sequence.Cast<object?>().ToList() : values;
        foreach (var entry in PythonBuiltins.Iterate(entries))
        {
            if (!PythonBuiltins.IsTruthy(entry))
            {
                continue;
            }

            if (entry is not string raw)
            {
                throw new PythonAttributeException($"'{PythonBuiltins.TypeName(entry)}' object has no attribute 'strip'");
            }

            var text = PythonText.Strip(raw);
            var dot = text.IndexOf('.', StringComparison.Ordinal);
            if (dot < 0)
            {
                continue;
            }

            var table = PythonText.Strip(text[..dot]);
            var column = PythonText.Strip(text[(dot + 1)..]);
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
