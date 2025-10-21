using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using DriftBuster.Backend;
using DriftBuster.Backend.Models;

using Xunit;

namespace DriftBuster.Gui.Tests.Backend;

public sealed class DriftbusterBackendTests
{
    private readonly DriftbusterBackend _backend = new();

    [Fact]
    public async Task PingAsync_returns_pong()
    {
        var response = await _backend.PingAsync();
        Assert.Equal("pong", response);
    }

    [Fact]
    public async Task DiffAsync_builds_comparisons_and_serializes_raw_json()
    {
        var baseline = Path.GetTempFileName();
        var comparison = Path.GetTempFileName();

        try
        {
            File.WriteAllText(baseline, "alpha");
            File.WriteAllText(comparison, "beta");

            var result = await _backend.DiffAsync(new[] { baseline, comparison });

            Assert.Single(result.Comparisons);
            Assert.Contains("alpha", result.Comparisons[0].Plan.Before);
            Assert.Contains("beta", result.Comparisons[0].Plan.After);
            Assert.False(string.IsNullOrWhiteSpace(result.RawJson));
        }
        finally
        {
            File.Delete(baseline);
            File.Delete(comparison);
        }
    }

    [Fact]
    public async Task DiffAsync_throws_for_missing_comparisons()
    {
        var baseline = Path.GetTempFileName();
        try
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _backend.DiffAsync(new[] { baseline }));
            Assert.Contains("Provide at least two file paths", ex.Message);
        }
        finally
        {
            File.Delete(baseline);
        }
    }

    [Fact]
    public async Task HuntAsync_returns_hits_and_filters_by_pattern()
    {
        var directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        var file = Path.Combine(directory.FullName, "config.txt");
        File.WriteAllText(file, "server: backend-host.example.com\nversion: 1.2.3");

        try
        {
            var result = await _backend.HuntAsync(directory.FullName, pattern: null);
            Assert.NotEmpty(result.Hits);
            Assert.Contains(result.Hits, hit => hit.RelativePath.EndsWith("config.txt", StringComparison.OrdinalIgnoreCase));
            Assert.False(string.IsNullOrWhiteSpace(result.RawJson));

            var filtered = await _backend.HuntAsync(directory.FullName, pattern: "nomatch");
            Assert.Equal(0, filtered.Count);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task HuntAsync_throws_for_missing_path()
    {
        var ex = await Assert.ThrowsAsync<FileNotFoundException>(() => _backend.HuntAsync(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), null));
        Assert.Contains("Path does not exist", ex.Message);
    }

    [Fact]
    public async Task RunProfile_round_trip_saves_and_lists_profiles()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "DriftbusterTests", Guid.NewGuid().ToString("N"));
        var sourceDir = Directory.CreateDirectory(Path.Combine(baseDir, "sources"));
        var baselineFile = Path.Combine(sourceDir.FullName, "baseline.txt");
        var dataFile = Path.Combine(sourceDir.FullName, "data.txt");
        File.WriteAllText(baselineFile, "baseline");
        File.WriteAllText(dataFile, "data");

        try
        {
            var profile = new RunProfileDefinition
            {
                Name = "Profile One",
                Baseline = baselineFile,
                Sources = new[] { baselineFile, Path.Combine(sourceDir.FullName, "*.txt") },
                Options = new Dictionary<string, string> { ["key"] = "value" },
            };

            var result = await _backend.RunProfileAsync(profile, saveProfile: true, baseDir: baseDir);

            Assert.True(Directory.Exists(result.OutputDir));
            Assert.True(result.Files.Length >= 2);
            Assert.NotNull(result.Profile);

            var listed = await _backend.ListProfilesAsync(baseDir);
            Assert.Contains(listed.Profiles, p => p.Name == "Profile One");

            var savedProfilePath = Path.Combine(baseDir, "Profiles", "Profile-One", "profile.json");
            Assert.True(File.Exists(savedProfilePath));
        }
        finally
        {
            if (Directory.Exists(baseDir))
            {
                Directory.Delete(baseDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task SaveProfileAsync_requires_name()
    {
        var profile = new RunProfileDefinition { Name = "" };
        await Assert.ThrowsAsync<InvalidOperationException>(() => _backend.SaveProfileAsync(profile, baseDir: Path.GetTempPath()));
    }

    [Fact]
    public void EnumerateFilesSafely_returns_nested_files()
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "DriftbusterTests", Guid.NewGuid().ToString("N")));
        var nested = Directory.CreateDirectory(Path.Combine(root.FullName, "nested"));
        var file = Path.Combine(nested.FullName, "entry.txt");
        File.WriteAllText(file, "content");

        try
        {
            var method = typeof(DriftbusterBackend).GetMethod("EnumerateFilesSafely", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(method);

            var results = ((IEnumerable<string>)method!.Invoke(null, new object[] { root.FullName, CancellationToken.None })! ).ToArray();

            Assert.Contains(file, results);
        }
        finally
        {
            if (Directory.Exists(root.FullName))
            {
                Directory.Delete(root.FullName, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ListProfilesAsync_ignores_invalid_entries()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "DriftbusterTests", Guid.NewGuid().ToString("N"));
        var profilesRoot = Path.Combine(baseDir, "Profiles");
        Directory.CreateDirectory(profilesRoot);

        var validDir = Directory.CreateDirectory(Path.Combine(profilesRoot, "Valid"));
        var invalidDir = Directory.CreateDirectory(Path.Combine(profilesRoot, "Broken"));

        var profileDefinition = new RunProfileDefinition
        {
            Name = "Valid Profile",
            Sources = new[] { "config.json" },
        };

        var json = JsonSerializer.Serialize(profileDefinition, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(validDir.FullName, "profile.json"), json);
        File.WriteAllText(Path.Combine(invalidDir.FullName, "profile.json"), "{ invalid json");

        try
        {
            var result = await _backend.ListProfilesAsync(baseDir);

            Assert.Single(result.Profiles);
            Assert.Equal("Valid Profile", result.Profiles[0].Name);
        }
        finally
        {
            if (Directory.Exists(baseDir))
            {
                Directory.Delete(baseDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task RunServerScansAsync_executes_multi_server_runner()
    {
        var backend = new DriftbusterBackend();
        var sampleRoot = Path.Combine("samples", "multi-server");

        var plans = new[]
        {
            new ServerScanPlan
            {
                HostId = "baseline",
                Label = "server01",
                Scope = ServerScanScope.CustomRoots,
                Roots = new[] { Path.Combine(sampleRoot, "server01") },
                Baseline = new ServerScanBaselinePreference { IsPreferred = true, Priority = 10, Role = "auto" },
                Export = new ServerScanExportOptions(),
            },
            new ServerScanPlan
            {
                HostId = "drift",
                Label = "server02",
                Scope = ServerScanScope.CustomRoots,
                Roots = new[] { Path.Combine(sampleRoot, "server02") },
                Baseline = new ServerScanBaselinePreference { IsPreferred = false, Priority = 5, Role = "auto" },
                Export = new ServerScanExportOptions(),
            },
        };

        var response = await backend.RunServerScansAsync(plans, progress: null, CancellationToken.None);

        Assert.Equal("multi-server.v1", response.Version);
        Assert.Equal(2, response.Results.Length);
        Assert.Contains(response.Results, result => result.HostId == "baseline" && result.Status == ServerScanStatus.Succeeded && result.Availability == ServerAvailabilityStatus.Found);
        Assert.Contains(response.Results, result => result.HostId == "drift" && result.Status == ServerScanStatus.Succeeded && result.Availability == ServerAvailabilityStatus.Found);
        Assert.NotEmpty(response.Catalog);
        var appEntry = response.Catalog.First(entry => entry.DisplayName.EndsWith("appsettings.json", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(2, appEntry.PresentHosts.Length);
        Assert.NotEmpty(response.Drilldown);
    }
}
