using System;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace DriftBuster.Gui.Services;

/// <summary>
/// Minimal <see cref="ILogger"/> implementation that records structured telemetry
/// as JSON to a single file. Designed for deterministic headless runs that rely on
/// log snapshots rather than interactive viewers.
/// </summary>
/// <typeparam name="T">Category type for the logger.</typeparam>
public sealed class FileJsonLogger<T> : ILogger<T>
{
    private readonly string _path;
    private readonly object _syncRoot = new();
    private readonly JsonSerializerOptions _serializerOptions;

    public FileJsonLogger(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("File path must be provided.", nameof(path));
        }

        _path = path;
        _serializerOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
        };
    }

    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
        {
            return;
        }

        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var entry = new FileJsonLogEntry
        {
            Timestamp = DateTimeOffset.UtcNow,
            Level = logLevel.ToString(),
            EventId = eventId.Id,
            EventName = string.IsNullOrWhiteSpace(eventId.Name) ? null : eventId.Name,
            Category = typeof(T).FullName ?? typeof(T).Name,
            Message = formatter(state, exception),
            State = state,
            Exception = exception?.ToString(),
        };

        var payload = JsonSerializer.Serialize(entry, _serializerOptions);
        lock (_syncRoot)
        {
            File.WriteAllText(_path, payload);
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();

        public void Dispose()
        {
        }
    }

    private sealed class FileJsonLogEntry
    {
        public DateTimeOffset Timestamp { get; init; }

        public string Level { get; init; } = string.Empty;

        public int EventId { get; init; }

        public string? EventName { get; init; }

        public string Category { get; init; } = string.Empty;

        public string Message { get; init; } = string.Empty;

        public object? State { get; init; }

        public string? Exception { get; init; }
    }
}
