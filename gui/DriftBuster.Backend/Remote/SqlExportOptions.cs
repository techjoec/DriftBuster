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

    /// <summary>
    /// The manifest file name under the output directory: <c>capture.py</c> always writes <c>sql-manifest.json</c>; <c>cli.py export-sql</c>
    /// takes <c>--manifest-name</c>.
    /// </summary>
    public string ManifestName { get; init; } = "sql-manifest.json";

    /// <summary>
    /// <c>cli.py export-sql</c>'s check before each export: a <c>--limit</c> of zero or less writes
    /// <c>error: --limit must be positive when provided</c>, sets exit code 1 and skips the database (<c>capture.py</c> leaves the limit to the
    /// snapshot builder, whose error is reported as a failed export).
    /// </summary>
    public bool LimitMustBePositive { get; init; }

    /// <summary><c>cli.py export-sql</c> reports <c>Manifest written to {path}</c> after writing the manifest; <c>capture.py</c> does not.</summary>
    public bool ReportManifestPath { get; init; }
}
