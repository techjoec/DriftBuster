namespace DriftBuster.Backend.Sql;

/// <summary>
/// What an export takes from each database: the tables (all when empty) less the excluded ones, columns to mask or hash by table,
/// at most <see cref="Limit"/> rows per table, the mask placeholder and the hash salt.
/// </summary>
public sealed record SqlExportSettings
{
    public const string DefaultPlaceholder = "[REDACTED]";

    public IReadOnlyList<string> Tables { get; init; } = [];

    public IReadOnlyList<string> ExcludeTables { get; init; } = [];

    public IReadOnlyDictionary<string, IReadOnlyList<string>> MaskedColumns { get; init; } = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, IReadOnlyList<string>> HashedColumns { get; init; } = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

    public int? Limit { get; init; }

    public string Placeholder { get; init; } = DefaultPlaceholder;

    public string HashSalt { get; init; } = string.Empty;

    /// <summary><c>table.column</c> arguments by table (split at the first dot, both parts trimmed); entries without both parts are refused.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> ParseColumns(IEnumerable<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var map = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            var dot = value.IndexOf('.', StringComparison.Ordinal);
            var table = dot < 0 ? string.Empty : value[..dot].Trim();
            var column = dot < 0 ? string.Empty : value[(dot + 1)..].Trim();
            if (table.Length == 0 || column.Length == 0)
            {
                throw new ArgumentException($"Column '{value}' must be written table.column.", nameof(values));
            }

            if (!map.TryGetValue(table, out var columns))
            {
                map[table] = columns = [];
            }

            columns.Add(column);
        }

        return map.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<string>)pair.Value, StringComparer.Ordinal);
    }
}
