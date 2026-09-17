using System.Numerics;

namespace DriftBuster.Backend.Remote;

/// <summary>The <c>capture.py export-sql</c> arguments (<c>argparse.Namespace</c>), each defaulting as the parser defaults it.</summary>
public sealed record SqlExportOptions
{
    /// <summary><c>database</c> (one or more): SQLite database paths.</summary>
    public IReadOnlyList<string> Database { get; init; } = [];

    /// <summary><c>--output-dir</c>.</summary>
    public string OutputDir { get; init; } = "sql-exports";

    /// <summary><c>--table</c> (repeatable).</summary>
    public IReadOnlyList<string> Table { get; init; } = [];

    /// <summary><c>--exclude-table</c> (repeatable).</summary>
    public IReadOnlyList<string> ExcludeTable { get; init; } = [];

    /// <summary><c>--mask-column</c> (repeatable, <c>table.column</c>).</summary>
    public IReadOnlyList<string> MaskColumn { get; init; } = [];

    /// <summary><c>--hash-column</c> (repeatable, <c>table.column</c>).</summary>
    public IReadOnlyList<string> HashColumn { get; init; } = [];

    /// <summary><c>--placeholder</c>.</summary>
    public string? Placeholder { get; init; } = "[REDACTED]";

    /// <summary><c>--hash-salt</c>; null counts as empty.</summary>
    public string? HashSalt { get; init; } = string.Empty;

    /// <summary><c>--limit</c>: rows exported per table; null exports every row.</summary>
    public BigInteger? Limit { get; init; }

    /// <summary><c>--prefix</c> for the snapshot file names.</summary>
    public string? Prefix { get; init; } = string.Empty;
}
