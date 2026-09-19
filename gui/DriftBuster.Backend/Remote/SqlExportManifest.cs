using DriftBuster.Backend.Sql;

namespace DriftBuster.Backend.Remote;

/// <summary>What one <c>sql-export</c> wrote (<c>sql-manifest.json</c>): each database's snapshot file, tables and row counts, and the settings.</summary>
public sealed record SqlExportManifest(DateTimeOffset CapturedAt, IReadOnlyList<SqlExportEntry> Exports, SqlExportSettings Settings);
