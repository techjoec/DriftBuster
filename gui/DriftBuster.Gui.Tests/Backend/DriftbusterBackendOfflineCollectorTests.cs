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
            Sources = new[] { "C:/logs" },
        };

        var packagePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.zip");
        var tempBase = Path.Combine(Path.GetTempPath(), "DriftbusterTests", Guid.NewGuid().ToString("N"));
        var scriptsDir = Path.Combine(tempBase, "scripts");
        Directory.CreateDirectory(scriptsDir);

        var scriptSource = LocateRepoFile("scripts", "driftbuster-offline-runner.ps1");
        File.Copy(scriptSource, Path.Combine(scriptsDir, "driftbuster-offline-runner.ps1"), overwrite: true);

        var rulesPath = LocateRepoFile("src", "driftbuster", "secret_rules.json");
        var backupPath = rulesPath + ".bak";
        var backupCreated = false;

        if (File.Exists(backupPath))
        {
            File.Delete(backupPath);
        }

        File.Move(rulesPath, backupPath);
        backupCreated = true;

        try
        {
            var request = new OfflineCollectorRequest
            {
                PackagePath = packagePath,
            };

            var result = await backend.PrepareOfflineCollectorAsync(profile, request, baseDir: tempBase);

            Assert.Equal(packagePath, result.PackagePath);
            Assert.True(File.Exists(packagePath));

            using var archive = ZipFile.OpenRead(packagePath);
            var entry = archive.GetEntry(result.ConfigFileName);
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
        finally
        {
            if (backupCreated && File.Exists(backupPath))
            {
                if (File.Exists(rulesPath))
                {
                    File.Delete(rulesPath);
                }

                File.Move(backupPath, rulesPath);
            }

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
}
