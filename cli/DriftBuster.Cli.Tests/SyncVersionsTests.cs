using System.Text;

using DriftBuster.Backend.Infrastructure;
using DriftBuster.Cli.Commands;

namespace DriftBuster.Cli.Tests;

/// <summary><see cref="VersionSync"/> (<c>driftbuster version</c>): file replacement and the update list.</summary>
public sealed class SyncVersionsTests : IDisposable
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);
    private readonly DirectoryInfo _tmp = Directory.CreateTempSubdirectory("driftbuster-sync-versions-");

    public void Dispose() => _tmp.Delete(recursive: true);

    [Fact]
    public void UpdateFileReplacesText()
    {
        var target = Path.Combine(_tmp.FullName, "config.txt");
        File.WriteAllText(target, "version = \"0.0.0\"\n", Utf8);

        VersionSync.UpdateFile(target, "version\\s*=\\s*\"[^\"]+\"", "version = \"1.2.3\"");

        File.ReadAllText(target, Utf8).Should().Be(OperatingSystem.IsWindows() ? "version = \"1.2.3\"\r\n" : "version = \"1.2.3\"\n");
    }

    [Fact]
    public void UpdateFileRaisesWhenPatternMissing()
    {
        var target = Path.Combine(_tmp.FullName, "config.txt");
        File.WriteAllText(target, "name = \"value\"\n", Utf8);

        var act = () => VersionSync.UpdateFile(target, "version", "replacement");

        act.Should().Throw<CommandExitException>().WithMessage($"No replacements made in {target} for pattern 'version'");
    }

    [Fact]
    public void MainInvokesExpectedUpdates()
    {
        var versions = new OrderedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["core"] = "1.0.0",
            ["catalog"] = "2.0.0",
            ["gui"] = "3.0.0",
            ["powershell"] = "4.0.0",
        };

        var recorded = VersionSync.Updates("/tmp/driftbuster", versions).ToList();

        recorded.Should().NotBeEmpty();
        recorded.Should().Contain(update => Path.GetFileName(update.Path) == "Directory.Build.props" && update.Replacement.Contains("1.0.0", StringComparison.Ordinal));
        recorded.Should().Contain(update => update.Pattern.Contains("GuiVersion", StringComparison.Ordinal));
        recorded.Should().Contain(update => Path.GetFileName(update.Path) == "DetectionCatalogData.cs" && string.Equals(update.Replacement, "Version: \"2.0.0\"", StringComparison.Ordinal));
        recorded.Select(update => Path.GetFileName(update.Path)).Distinct(StringComparer.Ordinal).Should().BeEquivalentTo(
            "Directory.Build.props", "GuiVersion.props", "DriftBuster.psd1", "DetectionCatalogData.cs", "CatalogTests.cs", "DetectorTests.cs",
            "detection-types.md", "xml-config-diffs.md", "INDIVIDUAL.md", "ENTITY.md");
        recorded.Should().Contain(update => Path.GetFileName(update.Path) == "DriftBuster.psd1" && update.Replacement.Contains("4.0.0", StringComparison.Ordinal));
        recorded.Should().Contain(update => update.Pattern.Contains("BackendVersion", StringComparison.Ordinal) && update.Replacement.Contains("1.0.0", StringComparison.Ordinal));
    }
}
