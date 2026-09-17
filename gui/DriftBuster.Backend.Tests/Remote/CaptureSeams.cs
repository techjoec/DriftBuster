using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Remote;
using DriftBuster.Backend.Sql;

namespace DriftBuster.Backend.Tests.Remote;

/// <summary>
/// Swaps the process-wide seams the capture commands' environment reads go through (<see cref="CaptureRunner"/>'s clock, monotonic
/// timer, host name and environment, and <see cref="SqliteSnapshots.UtcNow"/>) for queues and values, restoring them on dispose. Both
/// clocks draw from one queue.
/// </summary>
internal sealed class CaptureSeams : IDisposable
{
    private readonly Func<DateTimeOffset> _utcNow = CaptureRunner.UtcNow;
    private readonly Func<double> _monotonic = CaptureRunner.Monotonic;
    private readonly Func<string> _hostName = CaptureRunner.HostName;
    private readonly Func<string, string?> _environment = CaptureRunner.GetEnvironmentVariable;
    private readonly Func<DateTimeOffset> _sqlUtcNow = SqliteSnapshots.UtcNow;

    /// <summary>Installs the case's <c>now</c>, <c>monotonic</c>, <c>host</c> and <c>env</c> values (absent ones leave empty queues).</summary>
    public CaptureSeams(OrderedDictionary<string, object?> entry)
    {
        var now = new Queue<DateTimeOffset>(List(entry, "now").Select(stamp => IsoTimestamp.TryParse((string)stamp!, out var instant) ? instant : throw new FormatException((string)stamp!)));
        var clock = new Queue<double>(List(entry, "monotonic").Select(EngineBuiltins.Float));
        var environment = entry.GetValueOrDefault("env") as OrderedDictionary<string, object?> ?? new OrderedDictionary<string, object?>(StringComparer.Ordinal);
        var host = entry.GetValueOrDefault("host") as string ?? "host";
        CaptureRunner.UtcNow = now.Dequeue;
        SqliteSnapshots.UtcNow = now.Dequeue;
        CaptureRunner.Monotonic = clock.Dequeue;
        CaptureRunner.HostName = () => host;
        CaptureRunner.GetEnvironmentVariable = name => environment.GetValueOrDefault(name) as string;
    }

    public void Dispose()
    {
        CaptureRunner.UtcNow = _utcNow;
        CaptureRunner.Monotonic = _monotonic;
        CaptureRunner.HostName = _hostName;
        CaptureRunner.GetEnvironmentVariable = _environment;
        SqliteSnapshots.UtcNow = _sqlUtcNow;
    }

    private static IEnumerable<object?> List(OrderedDictionary<string, object?> entry, string key)
        => entry.GetValueOrDefault(key) as List<object?> ?? [];
}
