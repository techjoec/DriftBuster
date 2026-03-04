using System;
using System.IO;
using System.Text.Json;

using AwesomeAssertions;

using DriftBuster.Gui.Services;

namespace DriftBuster.Gui.Tests.Services;

public sealed class DebugFileLoggerTests : IDisposable
{
    private readonly string _tempRoot;

    public DebugFileLoggerTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "driftbuster-debug-log-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_requires_non_empty_path(string? path)
    {
        Action act = () => _ = new DebugFileLogger(path!);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Write_appends_json_line()
    {
        var path = Path.Combine(_tempRoot, "logs", "debug.jsonl");
        var logger = new DebugFileLogger(path);

        logger.Write("automation", "started", new { Pipe = "test" });

        File.Exists(path).Should().BeTrue();
        var lines = File.ReadAllLines(path);
        lines.Should().ContainSingle();

        using var payload = JsonDocument.Parse(lines[0]);
        var root = payload.RootElement;
        root.GetProperty("category").GetString().Should().Be("automation");
        root.GetProperty("message").GetString().Should().Be("started");
        root.GetProperty("detail").GetProperty("pipe").GetString().Should().Be("test");
    }

    [Fact]
    public void Write_skips_unserializable_payload()
    {
        var path = Path.Combine(_tempRoot, "debug.jsonl");
        var logger = new DebugFileLogger(path);
        var cyclic = new CyclicDetail();
        cyclic.Self = cyclic;

        logger.Write("cycle", "serialize", cyclic);

        File.Exists(path).Should().BeFalse();
    }

    [Fact]
    public void Write_stops_appending_when_file_cap_is_reached()
    {
        var path = Path.Combine(_tempRoot, "debug.jsonl");
        var seed = new byte[(10 * 1024 * 1024) + 32];
        File.WriteAllBytes(path, seed);
        var initialLength = new FileInfo(path).Length;

        var logger = new DebugFileLogger(path);

        logger.Write("cap", "first");
        logger.Write("cap", "second");

        var finalLength = new FileInfo(path).Length;
        finalLength.Should().Be(initialLength);
    }

    [Fact]
    public void Write_handles_invalid_path_without_throwing()
    {
        var path = Path.Combine(_tempRoot, "bad" + '\0' + "name.jsonl");
        var logger = new DebugFileLogger(path);

        Action act = () => logger.Write("io", "failure");

        act.Should().NotThrow();
    }

    [Fact]
    public void DefaultLogPath_targets_logs_debug_jsonl()
    {
        var path = DebugFileLogger.DefaultLogPath();

        path.Should().EndWith(Path.Combine("logs", "debug.jsonl"));
    }

    private sealed class CyclicDetail
    {
        public CyclicDetail? Self { get; set; }
    }
}
