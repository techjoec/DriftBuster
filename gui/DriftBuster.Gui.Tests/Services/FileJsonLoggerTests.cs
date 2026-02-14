using System;
using System.IO;
using System.Text.Json;

using DriftBuster.Gui.Services;

using Microsoft.Extensions.Logging;

namespace DriftBuster.Gui.Tests.Services;

public sealed class FileJsonLoggerTests : IDisposable
{
    private readonly string _tempDir;

    public FileJsonLoggerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "DriftbusterTests", "Logger", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_rejects_invalid_path(string? path)
    {
        Action act = () => new FileJsonLogger<FileJsonLoggerTests>(path!);
        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(LogLevel.Trace, false)]
    [InlineData(LogLevel.Debug, false)]
    [InlineData(LogLevel.Information, true)]
    [InlineData(LogLevel.Warning, true)]
    [InlineData(LogLevel.Error, true)]
    [InlineData(LogLevel.Critical, true)]
    public void IsEnabled_filters_below_information(LogLevel level, bool expected)
    {
        var logger = new FileJsonLogger<FileJsonLoggerTests>(Path.Combine(_tempDir, "test.json"));
        logger.IsEnabled(level).Should().Be(expected);
    }

    [Fact]
    public void Log_writes_structured_json_to_file()
    {
        var path = Path.Combine(_tempDir, "entry.json");
        var logger = new FileJsonLogger<FileJsonLoggerTests>(path);

        logger.LogInformation(new EventId(42, "TestEvent"), "Hello {Name}", "World");

        File.Exists(path).Should().BeTrue();
        var json = File.ReadAllText(path);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        root.GetProperty("level").GetString().Should().Be("Information");
        root.GetProperty("eventId").GetInt32().Should().Be(42);
        root.GetProperty("eventName").GetString().Should().Be("TestEvent");
        root.GetProperty("category").GetString().Should().Contain(nameof(FileJsonLoggerTests));
        root.GetProperty("message").GetString().Should().Contain("Hello");
    }

    [Fact]
    public void Log_below_information_does_not_write()
    {
        var path = Path.Combine(_tempDir, "debug.json");
        var logger = new FileJsonLogger<FileJsonLoggerTests>(path);

        logger.LogDebug("Should not write");

        File.Exists(path).Should().BeFalse();
    }

    [Fact]
    public void Log_creates_parent_directory_if_missing()
    {
        var path = Path.Combine(_tempDir, "nested", "deep", "output.json");
        var logger = new FileJsonLogger<FileJsonLoggerTests>(path);

        logger.LogWarning("test");

        File.Exists(path).Should().BeTrue();
    }

    [Fact]
    public void Log_overwrites_previous_entry()
    {
        var path = Path.Combine(_tempDir, "overwrite.json");
        var logger = new FileJsonLogger<FileJsonLoggerTests>(path);

        logger.LogInformation("first");
        logger.LogInformation("second");

        var json = File.ReadAllText(path);
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("message").GetString().Should().Contain("second");
    }

    [Fact]
    public void Log_includes_exception_when_present()
    {
        var path = Path.Combine(_tempDir, "exception.json");
        var logger = new FileJsonLogger<FileJsonLoggerTests>(path);
        var ex = new InvalidOperationException("boom");

        logger.Log(LogLevel.Error, new EventId(0), "fail", ex, (s, e) => $"{s}: {e?.Message}");

        var json = File.ReadAllText(path);
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("exception").GetString().Should().Contain("boom");
    }

    [Fact]
    public void BeginScope_returns_non_null_disposable()
    {
        var logger = new FileJsonLogger<FileJsonLoggerTests>(Path.Combine(_tempDir, "scope.json"));
        using var scope = logger.BeginScope("test-scope");
        scope.Should().NotBeNull();
    }
}
