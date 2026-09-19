using System.Globalization;
using System.Security.Cryptography;

using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Profiles.Run;

namespace DriftBuster.Cli.Commands;

/// <summary>
/// <c>driftbuster release stage-portable</c>: publishes the GUI as a
/// framework-dependent single file, copies the publish output to
/// <c>artifacts/gui-packaging/portable/DriftBuster.Gui-&lt;gui version&gt;-win11-portable-debug-&lt;timestamp&gt;</c> with debug launchers,
/// zips it with a <c>.sha256</c> beside the zip, and replaces the stage directory with the bundle. The GUI runs every feature in process.
/// </summary>
internal static partial class StagePortable
{
    public const string DefaultStageDir = "artifacts/gui-packaging/portable/staged";

    public const string TargetFramework = "net10.0";

    public static int Run(
        StagePortableOptions options, string root, string cwd, TextWriter stdout, Func<string, string, string, string> buildPublish)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(buildPublish);
        if (!TextModeFile.SamePath(cwd, root))
        {
            throw new CommandExitException($"Run this script from repository root: {root}");
        }

        var timestamp = string.IsNullOrEmpty(options.Timestamp)
            ? DateTime.UtcNow.ToString("yyyyMMdd-HHmmss'Z'", CultureInfo.InvariantCulture)
            : options.Timestamp;
        var version = ReadGuiVersion(root);
        var publishDir = buildPublish(root, options.Configuration, options.Rid);

        var portableRoot = Path.Combine(root, "artifacts", "gui-packaging", "portable");
        Directory.CreateDirectory(portableRoot);
        var bundleDir = Path.Combine(portableRoot, $"DriftBuster.Gui-{version}-win11-portable-debug-{timestamp}");
        if (TextModeFile.Exists(bundleDir))
        {
            Directory.Delete(bundleDir, recursive: true);
        }

        CopyTree(publishDir, bundleDir);
        WriteDebugLaunchers(bundleDir);
        var zipPath = ZipBundle(bundleDir);
        var shaPath = WriteSha256(zipPath);

        var stageDir = EnginePath.ExpandUser(LexicalPath.Str(options.StageDir));
        StageBundle(bundleDir, stageDir);

        ConsoleText.Print(stdout, "\nPortable debug bundle ready:");
        ConsoleText.Print(stdout, $" - Bundle dir: {bundleDir}");
        ConsoleText.Print(stdout, $" - Zip: {zipPath}");
        ConsoleText.Print(stdout, $" - Zip SHA256: {shaPath}");
        ConsoleText.Print(stdout, $" - Staged run dir: {stageDir}");
        ConsoleText.Print(stdout, $" - Launch: {LexicalPath.Join(stageDir, "Run-DriftBuster-Debug.cmd")}");
        return 0;
    }

    /// <summary>The trimmed <c>gui</c> version from <c>versions.json</c>.</summary>
    public static string ReadGuiVersion(string root)
    {
        var version = ComponentVersions.Load(root).Gui.Trim();
        return version.Length == 0 ? throw new CommandExitException($"Missing GUI version in {Path.Combine(root, "versions.json")}") : version;
    }

    /// <summary>The single-file publish and its output directory.</summary>
    public static string BuildPublish(
        string root, string configuration, string rid, TextWriter stdout, Func<IReadOnlyList<string>, string?, int> launcher)
    {
        var project = Path.Combine(root, "gui", "DriftBuster.Gui", "DriftBuster.Gui.csproj");
        ToolProcess.Run(
            [
                "dotnet", "publish", project, "-c", configuration, "-r", rid,
                "/p:PublishSingleFile=true", "/p:SelfContained=true", "/p:IncludeNativeLibrariesForSelfExtract=true",
            ],
            root,
            "->",
            stdout,
            launcher);
        var publishDir = Path.Combine(root, "gui", "DriftBuster.Gui", "bin", configuration, TargetFramework, rid, "publish");
        return Directory.Exists(publishDir) ? publishDir : throw new CommandExitException($"Publish output not found: {publishDir}");
    }

    /// <summary><c>write_sha256(path)</c>: <c>{digest}  {path}</c> in <c>{path}.sha256</c>.</summary>
    public static string WriteSha256(string path)
    {
        var digest = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
        var shaPath = $"{path}.sha256";
        TextModeFile.WriteText(shaPath, $"{digest}  {path}\n");
        return shaPath;
    }
}
