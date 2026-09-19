using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

using DriftBuster.Backend;
using DriftBuster.Backend.Models;
using DriftBuster.Backend.Profiles.Run;

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
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _backend.DiffAsync(new[] { dir.FullName, file }, TestContext.Current.CancellationToken));
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
            var ex = await Assert.ThrowsAsync<FileNotFoundException>(() => _backend.DiffAsync(new[] { baseline, missing }, TestContext.Current.CancellationToken));
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
                Sources = new[] { new RunProfileSource { Path = baseline }, new RunProfileSource { Path = missingPath } },
            };

            await Assert.ThrowsAsync<RunProfileException>(() => _backend.RunProfileAsync(profile, saveProfile: false, baseDir: baseDir, cancellationToken: TestContext.Current.CancellationToken));
        }
        finally
        {
            if (Directory.Exists(baseDir)) Directory.Delete(baseDir, recursive: true);
        }
    }

    [Fact]
    public async Task RunProfile_redacts_secrets_and_honours_structured_sources()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "DriftbusterTests", Guid.NewGuid().ToString("N"));
        var sourceDir = Directory.CreateDirectory(Path.Combine(baseDir, "src"));
        var secretFile = Path.Combine(sourceDir.FullName, "config.txt");
        File.WriteAllText(secretFile, "password = Hunter12345\n");
        var logs = Directory.CreateDirectory(Path.Combine(sourceDir.FullName, "logs"));
        File.WriteAllText(Path.Combine(logs.FullName, "keep.log"), "keep");
        File.WriteAllText(Path.Combine(logs.FullName, "skip.tmp"), "skip");

        try
        {
            var profile = new RunProfileDefinition
            {
                Name = "structured",
                Sources = new[]
                {
                    new RunProfileSource { Path = secretFile },
                    new RunProfileSource { Path = logs.FullName, Alias = "logs", Exclude = new[] { "*.tmp" } },
                    new RunProfileSource { Path = Path.Combine(baseDir, "missing", "*.log"), Optional = true },
                },
            };

            var result = await _backend.RunProfileAsync(profile, saveProfile: true, baseDir: baseDir, timestamp: "20240101T000000Z", cancellationToken: TestContext.Current.CancellationToken);

            result.Files.Select(file => file.Destination[(result.OutputDir.Length + 1)..].Replace('\\', '/'))
                .Should().Equal("source_00/config.txt", "logs/keep.log");
            File.ReadAllText(result.Files[0].Destination).Should().Contain("[SECRET]").And.NotContain("Hunter12345");
            var profileJson = File.ReadAllText(Path.Combine(baseDir, "Profiles", "structured", "profile.json"));
            profileJson.Should().Contain("\"alias\": \"logs\"").And.Contain("\"optional\": true");
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

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => backend.PrepareOfflineCollectorAsync(profile, request, baseDir, TestContext.Current.CancellationToken));
            ex.Message.Should().Contain("must not include path separators");
        }
        finally
        {
            if (File.Exists(packagePath)) File.Delete(packagePath);
            if (Directory.Exists(baseDir)) Directory.Delete(baseDir, recursive: true);
        }
    }
}

