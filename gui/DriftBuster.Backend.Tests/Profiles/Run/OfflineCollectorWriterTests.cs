using System.IO.Compression;
using System.Text.Json;

using DriftBuster.Backend.Models;
using DriftBuster.Backend.Profiles.Run;

namespace DriftBuster.Backend.Tests.Profiles.Run;

/// <summary>
/// The offline collector package (no Python oracle): the config carries each structured source in the shape
/// scripts/driftbuster-offline-runner.ps1 reads, and the package holds that runner script.
/// </summary>
public sealed class OfflineCollectorWriterTests : IDisposable
{
    private readonly DirectoryInfo _tmp = Directory.CreateTempSubdirectory("driftbuster-offline-writer-");

    public void Dispose() => _tmp.Delete(recursive: true);

    [Fact]
    public void PreparePackagesTheRunnerAndItsConfig()
    {
        var profile = new RunProfileDefinition
        {
            Name = "field kit",
            Description = "collect",
            Sources =
            [
                new RunProfileSource(" C:/logs "),
                new RunProfileSource("C:/data") { Alias = "  ", Optional = true, Exclude = ["*.tmp", ""] },
                new RunProfileSource(" "),
            ],
            Options = new Dictionary<string, string>(StringComparer.Ordinal) { ["k"] = "v" },
            SecretScanner = new SecretScannerOptions { IgnoreRules = ["a", " a ", ""], IgnorePatterns = ["P"] },
        };
        var packagePath = Path.Combine(_tmp.FullName, "out", "kit.zip");
        var request = new OfflineCollectorRequest { PackagePath = packagePath, Metadata = new Dictionary<string, string>(StringComparer.Ordinal) { [" site "] = "hq", [" "] = "x" } };

        var result = OfflineCollectorWriter.Prepare(profile, request, RepoPaths.Root, TestContext.Current.CancellationToken);

        result.PackagePath.Should().Be(packagePath);
        result.ConfigFileName.Should().Be("field-kit.offline.config.json");
        result.ScriptFileName.Should().Be(OfflineCollectorWriter.ScriptFileName);
        using var archive = ZipFile.OpenRead(packagePath);
        archive.Entries.Select(entry => entry.FullName).Order(StringComparer.Ordinal).Should().Equal(OfflineCollectorWriter.ScriptFileName, "field-kit.offline.config.json");
        using var stream = archive.GetEntry(result.ConfigFileName)!.Open();
        using var document = JsonDocument.Parse(stream);
        var config = document.RootElement;
        var profileElement = config.GetProperty("profile");
        profileElement.GetProperty("baseline").GetString().Should().Be("C:/logs");
        JsonSerializer.Serialize(profileElement.GetProperty("sources")).Should().Be(
            """[{"path":"C:/logs","optional":false,"exclude":[]},{"path":"C:/data","optional":true,"exclude":["*.tmp",""]}]""");
        JsonSerializer.Serialize(profileElement.GetProperty("secret_scanner").GetProperty("ignore_rules")).Should().Be("""["a"]""");
        profileElement.GetProperty("secret_scanner").GetProperty("ruleset").GetProperty("rules").GetArrayLength().Should().BePositive();
        config.GetProperty("runner").GetProperty("package_name").GetString().Should().Be("field-kit-offline-results");
        config.GetProperty("metadata").GetProperty("site").GetString().Should().Be("hq");
    }

    [Fact]
    public void PrepareValidatesTheRequest()
    {
        var profile = new RunProfileDefinition { Name = "p", Sources = [new RunProfileSource("C:/logs")] };

        var noName = () => OfflineCollectorWriter.Prepare(new RunProfileDefinition { Name = " " }, new OfflineCollectorRequest { PackagePath = "x.zip" }, null, TestContext.Current.CancellationToken);
        noName.Should().Throw<InvalidOperationException>().WithMessage("Profile name is required.");

        var noPackage = () => OfflineCollectorWriter.Prepare(profile, new OfflineCollectorRequest(), null, TestContext.Current.CancellationToken);
        noPackage.Should().Throw<InvalidOperationException>().WithMessage("Package path is required.");

        var separator = () => OfflineCollectorWriter.Prepare(
            profile,
            new OfflineCollectorRequest { PackagePath = Path.Combine(_tmp.FullName, "p.zip"), ConfigFileName = "a/b.json" },
            RepoPaths.Root,
            TestContext.Current.CancellationToken);
        separator.Should().Throw<InvalidOperationException>().WithMessage("Config file name must not include path separators.");

        var named = OfflineCollectorWriter.Prepare(
            profile,
            new OfflineCollectorRequest { PackagePath = Path.Combine(_tmp.FullName, "p.zip"), ConfigFileName = " custom " },
            RepoPaths.Root,
            TestContext.Current.CancellationToken);
        named.ConfigFileName.Should().Be("custom.json");
    }
}
