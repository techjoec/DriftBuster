using DriftBuster.Backend.Models;

namespace DriftBuster.Cli.Commands;

/// <summary>One line <c>driftbuster multi-server</c> writes: a progress update, the result, or the error that ended the run.</summary>
internal sealed record MultiServerLine(string Type, ScanProgress? Progress = null, ServerScanResponse? Result = null, string? Message = null);
