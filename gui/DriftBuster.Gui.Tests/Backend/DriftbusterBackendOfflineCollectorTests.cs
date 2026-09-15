using System;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Threading.Tasks;

using DriftBuster.Backend;
using DriftBuster.Backend.Models;

using Xunit;

namespace DriftBuster.Gui.Tests.Backend;

[Collection("BackendTests")]
public class DriftbusterBackendOfflineCollectorTests
{
    public DriftbusterBackendOfflineCollectorTests(BackendDataRootFixture fixture)
    {
        _ = fixture;
    }

    [Fact]
    public async Task PrepareOfflineCollector_uses_embedded_secret_rules_when_rules_file_missing()
    {
        var backend = new DriftbusterBackend();
        var profile = new RunProfileDefinition
        {
            Name = "offline-test",
            Sources = new[] { new RunProfileSource("C:/logs") },
        };

        var packagePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.zip");
        var tempBase = Path.Combine(Path.GetTempPath(), "DriftbusterTests", Guid.NewGuid().ToString("N"));
        var scriptsDir = Path.Combine(tempBase, "scripts");
        Directory.CreateDirectory(scriptsDir);

        var scriptSource = LocateRepoFile("scripts", "driftbuster-offline-runner.ps1");
        File.Copy(scriptSource, Path.Combine(scriptsDir, "driftbuster-offline-runner.ps1"), overwrite: true);

        var rulesPath = LocateRepoFile("src", "driftbuster", "secret_rules.json");
        using var hiddenFile = TemporarilyHideFile(rulesPath);

        try
        {
            var request = new OfflineCollectorRequest
            {
                PackagePath = packagePath,
            };

            var result = await backend.PrepareOfflineCollectorAsync(profile, request, baseDir: tempBase, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(packagePath, result.PackagePath);
            Assert.True(File.Exists(packagePath));
            ValidateSecretRulesInZip(packagePath, result.ConfigFileName);
        }
        finally
        {
            if (File.Exists(packagePath))
            {
                File.Delete(packagePath);
            }

            if (Directory.Exists(tempBase))
            {
                Directory.Delete(tempBase, recursive: true);
            }
        }
    }

    [Fact]
    public async Task PrepareOfflineCollector_writes_structured_sources_in_the_runner_shape()
    {
        var backend = new DriftbusterBackend();
        var profile = new RunProfileDefinition
        {
            Name = "structured",
            Sources = new[]
            {
                new RunProfileSource("C:/logs/app.log"),
                new RunProfileSource("C:/data") { Alias = " data ", Optional = true, Exclude = new[] { "*.tmp", " ", "cache/*" } },
                new RunProfileSource("  "),
            },
        };

        var packagePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.zip");
        var tempBase = Path.Combine(Path.GetTempPath(), "DriftbusterTests", Guid.NewGuid().ToString("N"));
        var scriptsDir = Directory.CreateDirectory(Path.Combine(tempBase, "scripts"));
        File.Copy(LocateRepoFile("scripts", "driftbuster-offline-runner.ps1"), Path.Combine(scriptsDir.FullName, "driftbuster-offline-runner.ps1"), overwrite: true);

        try
        {
            var request = new OfflineCollectorRequest { PackagePath = packagePath };
            var result = await backend.PrepareOfflineCollectorAsync(profile, request, baseDir: tempBase, cancellationToken: TestContext.Current.CancellationToken);

            using var archive = ZipFile.OpenRead(packagePath);
            using var reader = new StreamReader(archive.GetEntry(result.ConfigFileName)!.Open());
            using var document = JsonDocument.Parse(reader.ReadToEnd());
            var profileElement = document.RootElement.GetProperty("profile");
            Assert.Equal("C:/logs/app.log", profileElement.GetProperty("baseline").GetString());

            var sources = profileElement.GetProperty("sources");
            Assert.Equal(2, sources.GetArrayLength());
            Assert.Equal("""{"path":"C:/logs/app.log","optional":false,"exclude":[]}""", Compact(sources[0]));
            Assert.Equal("""{"path":"C:/data","alias":"data","optional":true,"exclude":["*.tmp"," ","cache/*"]}""", Compact(sources[1]));
        }
        finally
        {
            if (File.Exists(packagePath))
            {
                File.Delete(packagePath);
            }

            if (Directory.Exists(tempBase))
            {
                Directory.Delete(tempBase, recursive: true);
            }
        }
    }

    private static string Compact(JsonElement element) => JsonSerializer.Serialize(element);

    private static IDisposable TemporarilyHideFile(string filePath)
    {
        var backupPath = filePath + ".bak";
        if (File.Exists(backupPath))
        {
            File.Delete(backupPath);
        }

        File.Move(filePath, backupPath);
        return new FileRestorer(filePath, backupPath);
    }

    private static void ValidateSecretRulesInZip(string packagePath, string configFileName)
    {
        using var archive = ZipFile.OpenRead(packagePath);
        var entry = archive.GetEntry(configFileName);
        Assert.NotNull(entry);

        using var stream = entry!.Open();
        using var reader = new StreamReader(stream);
        var json = reader.ReadToEnd();
        using var document = JsonDocument.Parse(json);

        var ruleset = document.RootElement
            .GetProperty("profile")
            .GetProperty("secret_scanner")
            .GetProperty("ruleset");

        Assert.True(ruleset.TryGetProperty("rules", out var rulesProperty));
        Assert.True(rulesProperty.GetArrayLength() > 0);
    }

    private static string LocateRepoFile(params string[] segments)
    {
        var relative = Path.Combine(segments);
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, relative);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        throw new FileNotFoundException($"Unable to locate '{relative}'.");
    }

    private sealed class FileRestorer : IDisposable
    {
        private readonly string _originalPath;
        private readonly string _backupPath;

        public FileRestorer(string originalPath, string backupPath)
        {
            _originalPath = originalPath;
            _backupPath = backupPath;
        }

        public void Dispose()
        {
            if (!File.Exists(_backupPath))
            {
                return;
            }

            if (File.Exists(_originalPath))
            {
                File.Delete(_originalPath);
            }

            File.Move(_backupPath, _originalPath);
        }
    }
}
