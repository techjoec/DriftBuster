using System.Text.Json;

using DriftBuster.Backend;

namespace DriftBuster.Gui.Services;

/// <summary>
/// Appending JSONL debug logger for diagnosing GUI interaction issues.
/// Each entry is one JSON line, suitable for <c>tail -f</c> monitoring.
/// Thread-safe, capped at ~10 MB per session to prevent disk fill.
/// </summary>
public sealed class DebugFileLogger
{
    private const long MaxFileSizeBytes = 10 * 1024 * 1024;

    private readonly string _path;
    private readonly Lock _syncRoot = new();
    private readonly JsonSerializerOptions _serializerOptions;
    private bool _capReached;

    public DebugFileLogger(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("File path must be provided.", nameof(path));
        }

        _path = path;
        _serializerOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };
    }

    public void Write(string category, string message, object? detail = null)
    {
        if (_capReached)
        {
            return;
        }

        var entry = new DebugLogEntry
        {
            Timestamp = DateTimeOffset.UtcNow,
            Category = category,
            Message = message,
            Detail = detail,
        };

        string line;
        try
        {
            line = JsonSerializer.Serialize(entry, _serializerOptions);
        }
        catch
        {
            // Serialization failure must never crash the app.
            return;
        }

        lock (_syncRoot)
        {
            if (_capReached)
            {
                return;
            }

            try
            {
                var directory = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                using var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read);
                if (stream.Length > MaxFileSizeBytes)
                {
                    _capReached = true;
                    return;
                }

                using var writer = new StreamWriter(stream);
                writer.WriteLine(line);
            }
            catch
            {
                // Logging failures must never crash the app.
            }
        }
    }

    public static string DefaultLogPath()
    {
        return Path.Combine(DriftbusterPaths.GetDataRoot(), "logs", "debug.jsonl");
    }

    private sealed class DebugLogEntry
    {
        public DateTimeOffset Timestamp { get; init; }

        public string Category { get; init; } = string.Empty;

        public string Message { get; init; } = string.Empty;

        public object? Detail { get; init; }
    }
}
