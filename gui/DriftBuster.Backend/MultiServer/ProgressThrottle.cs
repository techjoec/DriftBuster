using DriftBuster.Backend.Models;

namespace DriftBuster.Backend.MultiServer;

/// <summary>
/// <c>emit_progress</c>'s duplicate suppression, owned by one run (Python keeps the state process-wide): an update is dropped
/// when the host's previous update had the same status and message and was emitted less than
/// <see cref="IntervalSeconds"/> earlier.
/// </summary>
public sealed class ProgressThrottle
{
    /// <summary><c>_PROGRESS_THROTTLE_SECONDS</c>.</summary>
    public const double IntervalSeconds = 0.05;

    private readonly Dictionary<string, (double Timestamp, ServerScanStatus Status, string Message)> _last = new(StringComparer.Ordinal);

    /// <summary>
    /// Records the update at monotonic time <paramref name="now"/> (seconds) and returns true when it should be emitted; a
    /// suppressed update leaves the recorded time unchanged.
    /// </summary>
    public bool ShouldEmit(string hostId, ServerScanStatus status, string message, double now)
    {
        ArgumentNullException.ThrowIfNull(hostId);
        ArgumentNullException.ThrowIfNull(message);
        if (_last.TryGetValue(hostId, out var last)
            && last.Status == status
            && string.Equals(last.Message, message, StringComparison.Ordinal)
            && now - last.Timestamp < IntervalSeconds)
        {
            return false;
        }

        _last[hostId] = (now, status, message);
        return true;
    }

    /// <summary>
    /// Reports the update to <paramref name="progress"/> on the calling thread when <see cref="ShouldEmit"/> allows it; the
    /// throttle state is updated even without a consumer, as in Python.
    /// </summary>
    public void Report(IProgress<ScanProgress>? progress, string hostId, ServerScanStatus status, string message, double now, DateTimeOffset timestamp)
    {
        if (!ShouldEmit(hostId, status, message, now))
        {
            return;
        }

        progress?.Report(new ScanProgress
        {
            HostId = hostId,
            Status = status,
            Message = message,
            Timestamp = timestamp,
        });
    }
}
