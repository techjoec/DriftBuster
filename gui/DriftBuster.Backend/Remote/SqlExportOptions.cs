using System.Numerics;

namespace DriftBuster.Backend.Remote;

/// <summary>The SQL export arguments, each with the command's default.</summary>
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

    /// <summary>
    /// The manifest file name under the output directory: <c>capture export-sql</c> always writes <c>sql-manifest.json</c>;
    /// <c>sql-export</c> takes <c>--manifest-name</c>.
    /// </summary>
    public string ManifestName { get; init; } = "sql-manifest.json";

    /// <summary>
    /// <c>sql-export</c>'s check before each export: a <c>--limit</c> of zero or less writes
    /// <c>error: --limit must be positive when provided</c>, sets exit code 1 and skips the database (<c>capture export-sql</c> leaves the limit to the
    /// snapshot builder, whose error is reported as a failed export).
    /// </summary>
    public bool LimitMustBePositive { get; init; }

    /// <summary><c>sql-export</c> reports <c>Manifest written to {path}</c> after writing the manifest; <c>capture export-sql</c> does not.</summary>
    public bool ReportManifestPath { get; init; }
}
