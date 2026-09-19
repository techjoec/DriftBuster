using DriftBuster.Backend.Remote;

namespace DriftBuster.Backend.Models;

/// <summary>A comparison's exit code and printed summary, and the comparison when both snapshots existed.</summary>
public sealed record CaptureCompareResult(int ExitCode, string Output, string Errors, CaptureComparison? Comparison);
