using System;
using System.IO;

using AwesomeAssertions;

using DriftBuster.Backend;
using DriftBuster.Gui.Tests;

namespace DriftBuster.Gui.Tests.Backend;

[Collection(EnvironmentVariablesCollection.Name)]
public sealed class DriftbusterPathsTests : IDisposable
{
    private readonly string? _originalDataRoot;
    private readonly string? _originalXdgDataHome;
    private readonly string? _originalHome;
    private readonly string _tempRoot;

    public DriftbusterPathsTests()
    {
        _originalDataRoot = Environment.GetEnvironmentVariable("DRIFTBUSTER_DATA_ROOT");
        _originalXdgDataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        _originalHome = Environment.GetEnvironmentVariable("HOME");
        _tempRoot = Path.Combine(Path.GetTempPath(), "driftbuster-path-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("DRIFTBUSTER_DATA_ROOT", _originalDataRoot);
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", _originalXdgDataHome);
        Environment.SetEnvironmentVariable("HOME", _originalHome);

        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    [Fact]
    public void GetDataRoot_prefers_environment_override()
    {
        var overridePath = Path.Combine(_tempRoot, "override-data-root");
        Environment.SetEnvironmentVariable("DRIFTBUSTER_DATA_ROOT", $"  {overridePath}  ");

        var resolved = DriftbusterPaths.GetDataRoot();

        resolved.Should().Be(Path.GetFullPath(overridePath));
        Directory.Exists(resolved).Should().BeTrue();
    }

    [Fact]
    public void GetDataRoot_expands_tilde_in_environment_override()
    {
        Environment.SetEnvironmentVariable("DRIFTBUSTER_DATA_ROOT", "~/driftbuster-tilde-test");

        var resolved = DriftbusterPaths.GetDataRoot();
        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.Personal);

            resolved.StartsWith(home, StringComparison.OrdinalIgnoreCase).Should().BeTrue();
            resolved.Should().Contain("driftbuster-tilde-test");
        }
        finally
        {
            if (Directory.Exists(resolved))
            {
                Directory.Delete(resolved, recursive: true);
            }
        }
    }

    [Fact]
    public void GetDataRoot_uses_xdg_data_home_when_override_missing()
    {
        Environment.SetEnvironmentVariable("DRIFTBUSTER_DATA_ROOT", null);
        var xdgRoot = Path.Combine(_tempRoot, "xdg");
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", xdgRoot);

        var resolved = DriftbusterPaths.GetDataRoot();

        resolved.Should().Be(Path.Combine(xdgRoot, "DriftBuster"));
        Directory.Exists(resolved).Should().BeTrue();
    }

    [Fact]
    public void Cache_and_session_directories_append_non_blank_segments()
    {
        var baseRoot = Path.Combine(_tempRoot, "paths");
        Environment.SetEnvironmentVariable("DRIFTBUSTER_DATA_ROOT", baseRoot);

        var cache = DriftbusterPaths.GetCacheDirectory("diff-planner", "", "history");
        var sessions = DriftbusterPaths.GetSessionDirectory("multi-server", " ", "snapshots");

        cache.Should().Be(Path.Combine(baseRoot, "cache", "diff-planner", "history"));
        sessions.Should().Be(Path.Combine(baseRoot, "sessions", "multi-server", "snapshots"));
        Directory.Exists(cache).Should().BeTrue();
        Directory.Exists(sessions).Should().BeTrue();
    }
}
