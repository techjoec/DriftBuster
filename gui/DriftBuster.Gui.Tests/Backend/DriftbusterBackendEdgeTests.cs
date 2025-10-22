using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

using DriftBuster.Backend;
using DriftBuster.Backend.Models;

namespace DriftBuster.Gui.Tests.Backend;

[Collection("BackendTests")]
public sealed class DriftbusterBackendEdgeTests
{
    private readonly DriftbusterBackend _backend = new();

    public DriftbusterBackendEdgeTests(BackendDataRootFixture fixture)
    {
        _ = fixture;
    }

    [Fact]
    public async Task DiffAsync_throws_when_baseline_is_directory()
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        var file = Path.GetTempFileName();
        try
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _backend.DiffAsync(new[] { dir.FullName, file }));
            ex.Message.Should().Contain("Baseline path is not a file");
        }
        finally
        {
            if (Directory.Exists(dir.FullName)) Directory.Delete(dir.FullName, recursive: true);
            if (File.Exists(file)) File.Delete(file);
        }
    }

    [Fact]
    public async Task DiffAsync_throws_for_nonexistent_comparison_path()
    {
        var baseline = Path.GetTempFileName();
        var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing.txt");
        try
        {
            var ex = await Assert.ThrowsAsync<FileNotFoundException>(() => _backend.DiffAsync(new[] { baseline, missing }));
            ex.Message.Should().Contain("Path does not exist");
        }
        finally
        {
            if (File.Exists(baseline)) File.Delete(baseline);
        }
    }

    [Fact]
    public async Task RunProfile_throws_when_non_glob_source_missing()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "DriftbusterTests", Guid.NewGuid().ToString("N"));
        var sourceDir = Directory.CreateDirectory(Path.Combine(baseDir, "sources"));
        var baseline = Path.Combine(sourceDir.FullName, "baseline.txt");
        File.WriteAllText(baseline, "baseline");

        var missingPath = Path.Combine(sourceDir.FullName, "nope.dne");

        try
        {
            var profile = new RunProfileDefinition
            {
                Name = "edge-profile",
                Baseline = baseline,
                Sources = new[] { baseline, missingPath },
            };

            await Assert.ThrowsAsync<FileNotFoundException>(() => _backend.RunProfileAsync(profile, saveProfile: false, baseDir: baseDir));
        }
        finally
        {
            if (Directory.Exists(baseDir)) Directory.Delete(baseDir, recursive: true);
        }
    }

    [Fact]
    public async Task RunProfile_reorders_baseline_to_first_source()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "DriftbusterTests", Guid.NewGuid().ToString("N"));
        var sourceDir = Directory.CreateDirectory(Path.Combine(baseDir, "src"));
        var a = Path.Combine(sourceDir.FullName, "a.txt");
        var b = Path.Combine(sourceDir.FullName, "b.txt");
        File.WriteAllText(a, "A");
        File.WriteAllText(b, "B");

        try
        {
            // Put baseline second; expect it to be treated as first during copy (source_00)
            var profile = new RunProfileDefinition
            {
                Name = "reorder",
                Baseline = b,
                Sources = new[] { a, b },
            };

            var result = await _backend.RunProfileAsync(profile, saveProfile: false, baseDir: baseDir);
            result.Files.Should().NotBeEmpty();

            // Find entry for the baseline and assert it landed under source_00
            var baselineEntry = result.Files.FirstOrDefault(f => string.Equals(f.Source, b, StringComparison.Ordinal));
            baselineEntry.Should().NotBeNull();
            baselineEntry!.Destination.Replace('\\', '/').Should().Contain("/source_00/");

            // metadata.json should include baseline field
            var metadataPath = Path.Combine(result.OutputDir, "metadata.json");
            File.Exists(metadataPath).Should().BeTrue();
            var json = JsonDocument.Parse(File.ReadAllText(metadataPath));
            json.RootElement.GetProperty("baseline").GetString().Should().Be(b);
        }
        finally
        {
            if (Directory.Exists(baseDir)) Directory.Delete(baseDir, recursive: true);
        }
    }

    [Fact]
    public async Task PrepareOfflineCollector_throws_for_invalid_config_file_name()
    {
        var backend = new DriftbusterBackend();
        var profile = new RunProfileDefinition { Name = "invalid-config" };
        var packagePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.zip");
        var baseDir = Path.Combine(Path.GetTempPath(), "DriftbusterTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(baseDir);

        try
        {
            var request = new OfflineCollectorRequest
            {
                PackagePath = packagePath,
                ConfigFileName = "bad/name.json",
            };

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => backend.PrepareOfflineCollectorAsync(profile, request, baseDir));
            ex.Message.Should().Contain("must not include path separators");
        }
        finally
        {
            if (File.Exists(packagePath)) File.Delete(packagePath);
            if (Directory.Exists(baseDir)) Directory.Delete(baseDir, recursive: true);
        }
    }
}

