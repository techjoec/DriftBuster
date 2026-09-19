using System.Collections;
using System.Globalization;
using System.Numerics;

using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Profiles.Run;

namespace DriftBuster.Backend.Sql;

/// <summary>
/// A <c>sql_snapshot</c> source of an offline runner config: the database path, the manifest alias, whether a missing database is
/// skipped, the table filters, the mask and hash column maps, the row limit, the placeholder, the salt and the dialect (only
/// <c>sqlite</c>). Values are in the <see cref="EngineJson"/> domain.
/// </summary>
public sealed record OfflineSqlSnapshotSource(string Path)
{
    public const string DefaultPlaceholder = "[REDACTED]";

    public string? Alias { get; init; }

    public bool Optional { get; init; }

    public IReadOnlyList<string> Tables { get; init; } = [];

    public IReadOnlyList<string> ExcludeTables { get; init; } = [];

    public OrderedDictionary<string, IReadOnlyList<string>> MaskColumns { get; init; } = new(StringComparer.Ordinal);

    public OrderedDictionary<string, IReadOnlyList<string>> HashColumns { get; init; } = new(StringComparer.Ordinal);

    public BigInteger? Limit { get; init; }

    public string Placeholder { get; init; } = DefaultPlaceholder;

    public string HashSalt { get; init; } = string.Empty;

    public string Dialect { get; init; } = "sqlite";

    /// <summary>
    /// <c>payload["sql_snapshot"]</c> must be a mapping (the spec). Path: the first truthy of the spec's and the payload's <c>path</c>, not
    /// blank. Alias: the first truthy of the payload's and the spec's, stripped, blank dropped. <c>optional</c>: the payload's, else the
    /// spec's, as a truthiness test. <c>tables</c> / <c>exclude_tables</c>: a string is one name, a list gives each non-blank stripped item
    /// as text. Column maps through <see cref="NormaliseSnapshotColumns"/>. <c>limit</c>, when not null, an integer that must be positive.
    /// Placeholder and salt: the first truthy of the spec's and the payload's, as text. <c>dialect</c> lower-cased, must be <c>sqlite</c>.
    /// </summary>
    public static OfflineSqlSnapshotSource FromDict(IReadOnlyDictionary<string, object?> payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.GetValueOrDefault("sql_snapshot") is not IReadOnlyDictionary<string, object?> spec)
        {
            throw new InvalidDataException("sql_snapshot source requires an object payload");
        }

        var pathValue = FirstTruthy(spec.GetValueOrDefault("path"), payload.GetValueOrDefault("path"));
        if (!EngineBuiltins.IsTruthy(pathValue) || EngineText.Strip(EngineRepr.Str(pathValue)).Length == 0)
        {
            throw new InvalidDataException("sql_snapshot requires a 'path'.");
        }

        var aliasValue = FirstTruthy(payload.GetValueOrDefault("alias"), spec.GetValueOrDefault("alias"));
        var alias = EngineBuiltins.IsTruthy(aliasValue) && EngineText.Strip(EngineRepr.Str(aliasValue)).Length > 0
            ? EngineText.Strip(EngineRepr.Str(aliasValue))
            : null;
        var optional = EngineBuiltins.IsTruthy(payload.TryGetValue("optional", out var optionalValue) ? optionalValue : spec.GetValueOrDefault("optional", false));

        BigInteger? limit = null;
        if (spec.GetValueOrDefault("limit") is { } limitValue)
        {
            limit = EngineBuiltins.Int(limitValue);
            if (limit.Value.Sign <= 0)
            {
                throw new InvalidDataException("sql_snapshot limit must be positive if provided");
            }
        }

        // A falsy payload value falls through to the default too.
        var placeholder = EngineRepr.Str(FirstTruthy(FirstTruthy(spec.GetValueOrDefault("placeholder"), payload.GetValueOrDefault("placeholder")), DefaultPlaceholder));
        var hashSalt = EngineRepr.Str(FirstTruthy(FirstTruthy(spec.GetValueOrDefault("hash_salt"), payload.GetValueOrDefault("hash_salt")), string.Empty));
        var dialect = EngineText.Lower(EngineRepr.Str(FirstTruthy(spec.GetValueOrDefault("dialect"), "sqlite")));
        if (!string.Equals(dialect, "sqlite", StringComparison.Ordinal))
        {
            throw new InvalidDataException("sql_snapshot currently supports only the 'sqlite' dialect");
        }

        return new OfflineSqlSnapshotSource(EngineRepr.Str(pathValue))
        {
            Alias = alias,
            Optional = optional,
            Tables = StringTuple(spec.GetValueOrDefault("tables")),
            ExcludeTables = StringTuple(spec.GetValueOrDefault("exclude_tables")),
            MaskColumns = NormaliseSnapshotColumns(spec.GetValueOrDefault("mask_columns")),
            HashColumns = NormaliseSnapshotColumns(spec.GetValueOrDefault("hash_columns")),
            Limit = limit,
            Placeholder = placeholder,
            HashSalt = hashSalt,
            Dialect = dialect,
        };
    }

    // The first truthy operand, else the last.
    private static object? FirstTruthy(object? first, object? second) => EngineBuiltins.IsTruthy(first) ? first : second;

    // Nothing for a falsy value, one item for a string, else each item's non-blank stripped text.
    private static List<string> StringTuple(object? raw)
    {
        if (!EngineBuiltins.IsTruthy(raw))
        {
            return [];
        }

        if (raw is string text)
        {
            return [text];
        }

        return EngineBuiltins.Iterate(raw).Select(EngineRepr.Str).Select(EngineText.Strip).Where(item => item.Length > 0).ToList();
    }

    /// <summary>
    /// Nothing for a falsy value. A mapping gives, per truthy table key, each item's non-blank stripped text of a sequence value (a
    /// string is a sequence of its characters) or the stripped text of any other value, tables with no entries dropped. A sequence gives,
    /// per truthy entry whose stripped text holds a dot, the column after the first dot under the table before it, both stripped and
    /// non-blank, tables in first-seen order. Anything else gives nothing.
    /// </summary>
    public static OrderedDictionary<string, IReadOnlyList<string>> NormaliseSnapshotColumns(object? value)
    {
        var normalised = new OrderedDictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        if (!EngineBuiltins.IsTruthy(value))
        {
            return normalised;
        }

        if (value is IReadOnlyDictionary<string, object?> mapping)
        {
            foreach (var (table, columns) in mapping)
            {
                if (!EngineBuiltins.IsTruthy(table))
                {
                    continue;
                }

                var entries = IsSequence(columns)
                    ? EngineBuiltins.Iterate(columns).Select(EngineRepr.Str).Select(EngineText.Strip).Where(column => column.Length > 0).ToList()
                    : [EngineText.Strip(EngineRepr.Str(columns))];
                if (entries.Count > 0)
                {
                    normalised[table] = entries;
                }
            }

            return normalised;
        }

        if (IsSequence(value))
        {
            foreach (var (table, columns) in GroupDottedColumns(value))
            {
                normalised[table] = columns;
            }
        }

        return normalised;
    }

    // The sequence form: "table.column" entries grouped by table in first-seen order.
    private static OrderedDictionary<string, List<string>> GroupDottedColumns(object? value)
    {
        var grouped = new OrderedDictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var entry in EngineBuiltins.Iterate(value))
        {
            if (!EngineBuiltins.IsTruthy(entry))
            {
                continue;
            }

            var text = EngineText.Strip(EngineRepr.Str(entry));
            var dot = text.IndexOf('.', StringComparison.Ordinal);
            if (text.Length == 0 || dot < 0)
            {
                continue;
            }

            var table = EngineText.Strip(text[..dot]);
            var column = EngineText.Strip(text[(dot + 1)..]);
            if (table.Length == 0 || column.Length == 0)
            {
                continue;
            }

            if (!grouped.TryGetValue(table, out var list))
            {
                grouped[table] = list = [];
            }

            list.Add(column);
        }

        return grouped;
    }

    // A string or a list; a mapping is not a sequence.
    private static bool IsSequence(object? value) => value is string || (value is IList and not IReadOnlyDictionary<string, object?> and not byte[]);

    /// <summary>The safe alias, else the safe file stem of the path, else <c>sql_snapshot_NN</c>.</summary>
    public string DestinationName(int fallbackIndex)
    {
        if (!string.IsNullOrEmpty(Alias))
        {
            return RunProfileStore.SafeName(Alias);
        }

        var stem = Stem(Path);
        if (stem.Length > 0)
        {
            return RunProfileStore.SafeName(stem);
        }

        // Zero-padded to two characters, the sign counted.
        var index = fallbackIndex >= 0
            ? fallbackIndex.ToString("00", CultureInfo.InvariantCulture)
            : fallbackIndex.ToString(CultureInfo.InvariantCulture);
        return $"sql_snapshot_{index}";
    }

    // The file name less its suffix (the last dot that is neither the name's first nor its last character).
    private static string Stem(string path)
    {
        var parts = LexicalPath.Parts(path);
        var name = parts.Count > (LexicalPath.Anchor(path).Length > 0 ? 1 : 0) ? parts[^1] : string.Empty;
        var dot = name.LastIndexOf('.');
        return dot > 0 && dot < name.Length - 1 ? name[..dot] : name;
    }

    /// <summary>
    /// The <see cref="SqliteSnapshots.BuildSqliteSnapshot"/> arguments by name (<c>tables</c> and <c>exclude_tables</c> null when empty).
    /// </summary>
    public OrderedDictionary<string, object?> SnapshotKwargs() => new(StringComparer.Ordinal)
    {
        ["tables"] = Tables.Count > 0 ? Tables.Cast<object?>().ToList() : null,
        ["exclude_tables"] = ExcludeTables.Count > 0 ? ExcludeTables.Cast<object?>().ToList() : null,
        ["mask_columns"] = ColumnMap(MaskColumns),
        ["hash_columns"] = ColumnMap(HashColumns),
        ["limit"] = Limit is { } limit ? EngineValues.Narrow(limit) : null,
        ["placeholder"] = Placeholder,
        ["hash_salt"] = HashSalt,
    };

    /// <summary>The column map as a JSON-domain mapping of lists.</summary>
    public static OrderedDictionary<string, object?> ColumnMap(OrderedDictionary<string, IReadOnlyList<string>> columns)
    {
        ArgumentNullException.ThrowIfNull(columns);
        return new OrderedDictionary<string, object?>(
            columns.Select(pair => new KeyValuePair<string, object?>(pair.Key, pair.Value.Cast<object?>().ToList())),
            StringComparer.Ordinal);
    }
}
