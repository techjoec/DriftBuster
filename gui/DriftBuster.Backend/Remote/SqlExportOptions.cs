using DriftBuster.Backend.Sql;

namespace DriftBuster.Backend.Remote;

/// <summary>The databases to export, where to write them, and what each export takes.</summary>
public sealed record SqlExportOptions
{
    public IReadOnlyList<string> Databases { get; init; } = [];

    public string OutputDir { get; init; } = "sql-exports";

    public SqlExportSettings Settings { get; init; } = new();

    /// <summary>The snapshot file stem (with several databases, prefixed to each database's stem).</summary>
    public string? Prefix { get; init; }

    public string ManifestName { get; init; } = "sql-manifest.json";

    /// <summary>Print <c>Manifest written to</c> after the manifest is written.</summary>
    public bool ReportManifestPath { get; init; }
}
