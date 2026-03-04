namespace DriftBuster.Gui.Services;

/// <summary>
/// Static convenience accessor for the debug JSONL logger.
/// Enabled when <c>DRIFTBUSTER_DEBUG=1</c> is set.
/// Call <see cref="Trace"/> from anywhere without constructor injection.
/// </summary>
public static class DebugLog
{
    private static readonly bool s_enabled =
        string.Equals(
            Environment.GetEnvironmentVariable("DRIFTBUSTER_DEBUG"),
            "1",
            StringComparison.Ordinal);

    private static readonly Lazy<DebugFileLogger> s_logger =
        new(() => new DebugFileLogger(DebugFileLogger.DefaultLogPath()));

    public static bool IsEnabled => s_enabled;

    public static void Trace(string category, string message, object? detail = null)
    {
        if (!s_enabled)
        {
            return;
        }

        s_logger.Value.Write(category, message, detail);
    }
}
