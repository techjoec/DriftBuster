using System.Xml.Linq;

using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Cli.Tests;

/// <summary>
/// Both executables embed a Win32 manifest declaring <c>longPathAware</c>, so a path past <c>MAX_PATH</c> reaches native SQLite (and every
/// other unprefixed Win32 call); the execution level stays <c>asInvoker</c>.
/// </summary>
public sealed class ApplicationManifestTests
{
    private static readonly XNamespace Asm = "urn:schemas-microsoft-com:asm.v1";
    private static readonly XNamespace AsmV3 = "urn:schemas-microsoft-com:asm.v3";
    private static readonly XNamespace WindowsSettings = "http://schemas.microsoft.com/SMI/2016/WindowsSettings";

    [Theory]
    [InlineData("cli/DriftBuster.Cli/DriftBuster.Cli.csproj")]
    [InlineData("gui/DriftBuster.Gui/DriftBuster.Gui.csproj")]
    public void ExecutableDeclaresLongPathAwareAsInvoker(string project)
    {
        var projectPath = Path.Combine(RepositoryRoot.Require(), project);
        var manifestName = XDocument.Load(projectPath).Descendants("ApplicationManifest").Select(e => e.Value).Should().ContainSingle().Which;
        var manifest = XDocument.Load(Path.Combine(Path.GetDirectoryName(projectPath)!, manifestName));
        manifest.Root!.Name.Should().Be(Asm + "assembly");
        manifest.Descendants(AsmV3 + "requestedExecutionLevel").Should().ContainSingle().Which.Attribute("level")!.Value.Should().Be("asInvoker");
        manifest.Descendants(WindowsSettings + "longPathAware").Should().ContainSingle().Which.Value.Should().Be("true");
    }
}
