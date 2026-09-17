using System.Collections;
using System.Globalization;
using System.Numerics;

using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Profiles.Run;

namespace DriftBuster.Backend.Sql;

/// <summary>
/// <c>offline_runner.OfflineSqlSnapshotSource</c>: a <c>sql_snapshot</c> source of an offline runner profile. The database path, the
/// manifest alias, whether a missing database is skipped, the table filters, the mask and hash column maps, the row limit, the
/// placeholder, the salt and the dialect (only <c>sqlite</c>). Values are in the <see cref="EngineJson"/> domain.
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
    /// <c>OfflineSqlSnapshotSource.from_dict(payload)</c>: <c>payload["sql_snapshot"]</c> must be a mapping; the path is the first truthy
    /// of the spec's and the payload's <c>path</c> and must not be blank; the alias the first truthy of the payload's and the spec's,
    /// stripped, blank dropped; <c>optional</c> from the payload, else the spec, as <c>bool()</c>; <c>tables</c> and <c>exclude_tables</c>
    /// a str as one name or each non-blank stripped <c>str()</c> of an iterable; the column maps through
    /// <see cref="NormaliseSnapshotColumns"/>; <c>int(limit)</c> when not null, which must be positive; the placeholder and salt the first
    /// truthy of the spec's and the payload's as <c>str()</c>; <c>str(dialect).lower()</c>, which must be <c>sqlite</c>.
    /// </summary>
    /// <exception cref="EngineValueException">Python's <c>ValueError</c> text for each refusal.</exception>
    public static OfflineSqlSnapshotSource FromDict(IReadOnlyDictionary<string, object?> payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.GetValueOrDefault("sql_snapshot") is not IReadOnlyDictionary<string, object?> spec)
        {
            throw new EngineValueException("sql_snapshot source requires an object payload", nameof(payload));
        }

        var pathValue = FirstTruthy(spec.GetValueOrDefault("path"), payload.GetValueOrDefault("path"));
        if (!EngineBuiltins.IsTruthy(pathValue) || EngineText.Strip(EngineRepr.Str(pathValue)).Length == 0)
        {
            throw new EngineValueException("sql_snapshot requires a 'path'.", nameof(payload));
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
                throw new EngineValueException("sql_snapshot limit must be positive if provided", nameof(payload));
            }
        }

        // str(spec.get(key) or payload.get(key) or default): a falsy payload value falls through to the default too.
        var placeholder = EngineRepr.Str(FirstTruthy(FirstTruthy(spec.GetValueOrDefault("placeholder"), payload.GetValueOrDefault("placeholder")), DefaultPlaceholder));
        var hashSalt = EngineRepr.Str(FirstTruthy(FirstTruthy(spec.GetValueOrDefault("hash_salt"), payload.GetValueOrDefault("hash_salt")), string.Empty));
        var dialect = EngineText.Lower(EngineRepr.Str(FirstTruthy(spec.GetValueOrDefault("dialect"), "sqlite")));
        if (!string.Equals(dialect, "sqlite", StringComparison.Ordinal))
        {
            throw new EngineValueException("sql_snapshot currently supports only the 'sqlite' dialect", nameof(payload));
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

    // a or b: the first truthy operand, else the last.
    private static object? FirstTruthy(object? first, object? second) => EngineBuiltins.IsTruthy(first) ? first : second;

    // _string_tuple(key): () for a falsy value, (raw,) for a str, else the non-blank stripped str() of each item.
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
    /// <c>_normalise_snapshot_columns(value)</c>: nothing for a falsy value. A mapping gives, per truthy table key, the non-blank stripped
    /// <c>str()</c> of each item of a sequence value (a str value is a sequence of its characters), or the stripped <c>str()</c> of any other
    /// value, under <c>str(table)</c>, tables with no entries dropped. A sequence gives, per truthy entry whose stripped <c>str()</c> holds
    /// a dot, the column after the first dot under the table before it, both stripped and non-blank, tables in first-seen order.
    /// Anything else gives nothing.
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

    // isinstance(value, Sequence) over the JSON domain: a str or a list (a mapping is not a Sequence).
    private static bool IsSequence(object? value) => value is string || (value is IList and not IReadOnlyDictionary<string, object?> and not byte[]);

    /// <summary>
    /// <c>source.destination_name(fallback_index=fallbackIndex)</c>: the safe alias, else the safe <c>Path(path).stem</c>, else
    /// <c>sql_snapshot_NN</c>.
    /// </summary>
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

        // f"{index:02d}": zero-padded to two characters, the sign counted.
        var index = fallbackIndex >= 0
            ? fallbackIndex.ToString("00", CultureInfo.InvariantCulture)
            : fallbackIndex.ToString(CultureInfo.InvariantCulture);
        return $"sql_snapshot_{index}";
    }

    // PurePath(path).stem: the name less its suffix (the last dot that is neither the name's first nor its last character).
    private static string Stem(string path)
    {
        // PurePath.name: the last of the parts after the anchor, or "".
        var parts = EnginePurePath.Parts(path);
        var name = parts.Count > (EnginePurePath.Anchor(path).Length > 0 ? 1 : 0) ? parts[^1] : string.Empty;
        var dot = name.LastIndexOf('.');
        return dot > 0 && dot < name.Length - 1 ? name[..dot] : name;
    }

    /// <summary>
    /// <c>source.snapshot_kwargs()</c>: the <c>build_sqlite_snapshot</c> keyword arguments (<c>tables</c> and <c>exclude_tables</c> null
    /// when empty), in the order the method builds them.
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

    /// <summary>The column map as a JSON-domain mapping of lists (<c>{table: list(columns)}</c>).</summary>
    public static OrderedDictionary<string, object?> ColumnMap(OrderedDictionary<string, IReadOnlyList<string>> columns)
    {
        ArgumentNullException.ThrowIfNull(columns);
        return new OrderedDictionary<string, object?>(
            columns.Select(pair => new KeyValuePair<string, object?>(pair.Key, pair.Value.Cast<object?>().ToList())),
            StringComparer.Ordinal);
    }
}
