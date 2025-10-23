using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace DriftBuster.Gui.Tests.Headless;

internal static class HeadlessFontHealthTelemetry
{
    private static readonly object Sync = new();
    private static readonly Dictionary<string, ScenarioRecord> Records = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static bool _loaded;

    public static HeadlessFontHealthScenario BeginScenario(string scenarioName)
        => new(scenarioName);

    internal static void Commit(string scenarioName, bool succeeded, IReadOnlyDictionary<string, string?> metrics)
    {
        if (string.IsNullOrWhiteSpace(scenarioName))
        {
            return;
        }

        lock (Sync)
        {
            EnsureLoaded();

            if (!Records.TryGetValue(scenarioName, out var record))
            {
                record = new ScenarioRecord
                {
                    Name = scenarioName,
                };
            }

            record.TotalRuns += 1;
            if (succeeded)
            {
                record.Passes += 1;
            }
            else
            {
                record.Failures += 1;
            }

            record.LastStatus = succeeded ? "pass" : "fail";
            record.LastUpdated = DateTimeOffset.UtcNow;
            record.LastDetails = new Dictionary<string, string?>(metrics, StringComparer.OrdinalIgnoreCase);

            Records[scenarioName] = record;

            Save();
        }
    }

    private static void EnsureLoaded()
    {
        if (_loaded)
        {
            return;
        }

        var path = GetLogPath();
        if (File.Exists(path))
        {
            try
            {
                using var stream = File.OpenRead(path);
                var payload = JsonSerializer.Deserialize<HealthLog>(stream, SerializerOptions);
                if (payload?.Scenarios is { Length: > 0 })
                {
                    foreach (var record in payload.Scenarios.Where(r => !string.IsNullOrWhiteSpace(r?.Name)))
                    {
                        record!.LastDetails ??= new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                        Records[record.Name] = record;
                    }
                }
            }
            catch
            {
                Records.Clear();
            }
        }

        _loaded = true;
    }

    private static void Save()
    {
        var log = new HealthLog
        {
            GeneratedAt = DateTimeOffset.UtcNow,
            Scenarios = Records.Values
                .OrderBy(record => record.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
        };

        var path = GetLogPath();
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(log, SerializerOptions);
        File.WriteAllText(path, json);
    }

    private static string GetLogPath()
        => Path.Combine(GetRepositoryRoot(), "artifacts", "logs", "headless-font-health.json");

    private static string GetRepositoryRoot()
    {
        var path = AppContext.BaseDirectory;
        for (var i = 0; i < 5; i++)
        {
            path = Path.GetFullPath(Path.Combine(path, ".."));
        }

        return path;
    }

    private sealed class HealthLog
    {
        public DateTimeOffset GeneratedAt { get; set; }

        public ScenarioRecord[] Scenarios { get; set; } = Array.Empty<ScenarioRecord>();
    }

    private sealed class ScenarioRecord
    {
        public string Name { get; set; } = string.Empty;

        public int TotalRuns { get; set; }

        public int Passes { get; set; }

        public int Failures { get; set; }

        public string? LastStatus { get; set; }

        public DateTimeOffset? LastUpdated { get; set; }

        public Dictionary<string, string?>? LastDetails { get; set; }
    }

    internal sealed class HeadlessFontHealthScenario : IDisposable
    {
        private readonly string _name;
        private readonly Dictionary<string, string?> _metrics = new(StringComparer.OrdinalIgnoreCase);
        private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
        private bool _completed;

        public HeadlessFontHealthScenario(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("Scenario name must be provided.", nameof(name));
            }

            _name = name;
            _metrics["started_at"] = _startedAt.ToString("O", CultureInfo.InvariantCulture);
        }

        public void RecordMetric(string key, string? value)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return;
            }

            _metrics[key] = value;
        }

        public void MarkSuccess()
            => MarkOutcome(success: true, exception: null);

        public void MarkFailure(Exception? exception)
            => MarkOutcome(success: false, exception);

        private void MarkOutcome(bool success, Exception? exception)
        {
            if (_completed)
            {
                return;
            }

            _completed = true;
            _metrics["completed_at"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            if (!success && exception is not null)
            {
                _metrics["error"] = $"{exception.GetType().Name}: {exception.Message}";
            }

            Commit(_name, success, new Dictionary<string, string?>(_metrics, StringComparer.OrdinalIgnoreCase));
        }

        public void Dispose()
        {
            if (!_completed)
            {
                MarkFailure(null);
            }
        }
    }
}
