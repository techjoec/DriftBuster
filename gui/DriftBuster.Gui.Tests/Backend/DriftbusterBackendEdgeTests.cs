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
                Sources = new[] { new RunProfileSource(baseline), new RunProfileSource(missingPath) },
            };

            await Assert.ThrowsAsync<FileNotFoundException>(() => _backend.RunProfileAsync(profile, saveProfile: false, baseDir: baseDir, cancellationToken: TestContext.Current.CancellationToken));
        }
        finally
        {
            if (Directory.Exists(baseDir)) Directory.Delete(baseDir, recursive: true);
        }
    }

    [Fact]
    public async Task RunProfile_runs_a_stored_structured_profile_with_its_raw_option_values()
    {
        var root = Directory.CreateTempSubdirectory("driftbuster-facade-options-");
        try
        {
            // The same hand-written profile under two base directories: one run by the executor from the file, one through the facade.
            string Stage(string name)
            {
                var baseDir = Directory.CreateDirectory(Path.Combine(root.FullName, name)).FullName;
                var data = Directory.CreateDirectory(Path.Combine(baseDir, "data")).FullName;
                File.WriteAllText(Path.Combine(data, "app.ini"), "password='SuperSecret1234abcd'\n");
                var profileDir = Directory.CreateDirectory(Path.Combine(baseDir, "Profiles", "p")).FullName;
                File.WriteAllText(
                    Path.Combine(profileDir, "profile.json"),
                    "{\"name\": \"p\", \"sources\": [{\"path\": " + JsonSerializer.Serialize(data) + ", \"alias\": \"d\"}], "
                    + "\"options\": {\"secret_ignore_patterns\": [\"5\"], \"note\": 7}}");
                return baseDir;
            }

            var direct = Stage("direct");
            var facade = Stage("facade");
            var expected = RunProfileExecutor.ExecuteProfile(RunProfileStore.LoadProfile("p", direct), direct, "t", TestContext.Current.CancellationToken);

            var listed = (await _backend.ListProfilesAsync(facade, TestContext.Current.CancellationToken)).Profiles.Single();
            listed.Options["secret_ignore_patterns"].Should().Be("['5']");
            var result = await _backend.RunProfileAsync(listed, saveProfile: true, baseDir: facade, timestamp: "t", cancellationToken: TestContext.Current.CancellationToken);

            string Secrets(string outputDir) => JsonDocument.Parse(File.ReadAllText(Path.Combine(outputDir, "metadata.json"))).RootElement.GetProperty("secrets").GetRawText();
            Secrets(result.OutputDir).Should().Be(Secrets(expected.OutputDir));
            ((List<object?>)expected.Secrets!["findings"]!).Should().ContainSingle();
            ((List<object?>)expected.Secrets!["ignored_patterns"]!).Should().Equal("5");

            // An edited value runs with its text, as the model holds it.
            listed.Options["secret_ignore_patterns"] = "['6']";
            var edited = await _backend.RunProfileAsync(listed, saveProfile: false, baseDir: Stage("edited"), timestamp: "t", cancellationToken: TestContext.Current.CancellationToken);
            JsonDocument.Parse(Secrets(edited.OutputDir)).RootElement.GetProperty("ignored_patterns").EnumerateArray().Select(item => item.GetString()).Should().Equal("['6']");
        }
        finally
        {
            root.Delete(recursive: true);
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
                Sources = new[] { new RunProfileSource(a), new RunProfileSource(b) },
            };

            var result = await _backend.RunProfileAsync(profile, saveProfile: false, baseDir: baseDir, cancellationToken: TestContext.Current.CancellationToken);
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
                    new RunProfileSource(secretFile),
                    new RunProfileSource(logs.FullName) { Alias = "logs", Exclude = new[] { "*.tmp" } },
                    new RunProfileSource(Path.Combine(baseDir, "missing", "*.log")) { Optional = true },
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

