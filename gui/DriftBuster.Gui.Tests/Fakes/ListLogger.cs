using System;
using System.Collections.Generic;

using Microsoft.Extensions.Logging;

namespace DriftBuster.Gui.Tests.Fakes;

public sealed class ListLogger<T> : ILogger<T>
{
    private sealed class NoopScope : IDisposable
    {
        public static readonly NoopScope Instance = new();

        public void Dispose()
        {
        }
    }

    public List<LogEntry> Entries { get; } = new();

    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NoopScope.Instance;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        Entries.Add(new LogEntry(logLevel, eventId, state, exception, formatter(state, exception)));
    }

    public sealed record LogEntry(LogLevel Level, EventId EventId, object? State, Exception? Exception, string Message);
}
