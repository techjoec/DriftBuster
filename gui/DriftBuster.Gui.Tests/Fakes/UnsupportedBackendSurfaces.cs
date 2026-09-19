using System;
using System.Threading;
using System.Threading.Tasks;

using DriftBuster.Backend.Models;
using DriftBuster.Backend.Remote;

namespace DriftBuster.Gui.Tests.Fakes;

/// <summary>
/// The <see cref="DriftBuster.Backend.IDriftbusterBackend"/> surfaces the GUI never calls (capture, SQL export, registry and reports), each
/// refused as not supported. A test backend derives from it and implements the interface's GUI methods itself.
/// </summary>
internal abstract class UnsupportedBackendSurfaces
{
    public Task<SqlExportResult> ExportSqlSnapshotAsync(SqlExportOptions options, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("The GUI does not export SQL snapshots.");

    public Task<CaptureRunResult> RunCaptureAsync(CaptureRunOptions options, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("The GUI does not run captures.");

    public Task<CaptureCompareResult> CompareCapturesAsync(string baselinePath, string currentPath, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("The GUI does not compare captures.");

    public Task<RegistryAppListResult> ListRegistryAppsAsync(CancellationToken cancellationToken = default)
        => throw new NotSupportedException("The GUI does not list registry applications.");

    public Task<RegistrySearchResult> SearchRegistryAsync(RegistrySearchRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("The GUI does not search the registry.");

    public Task<ReportResult> BuildReportAsync(ReportRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("The GUI does not build reports.");
}
