using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Cli.Commands;

/// <summary>
/// <c>driftbuster release</c>: from the repository root, clears <c>build/</c>, runs the .NET test projects (unless <c>--skip-tests</c>),
/// publishes the console tool to <c>build/artifacts/cli/&lt;rid|framework&gt;</c>, publishes the GUI to
/// <c>build/artifacts/gui/&lt;rid|framework&gt;</c>, builds the Velopack installer unless
/// <c>--no-installer</c>, and for no runtime or a <c>win-</c> runtime stages the portable debug bundle in
/// <see cref="StagePortable.DefaultStageDir"/>.
/// </summary>
internal static class ReleaseBuild
{
    public static string CliArtifactDir(string root) => Path.Combine(root, "build", "artifacts", "cli");

    public static string GuiArtifactDir(string root) => Path.Combine(root, "build", "artifacts", "gui");

    /// <summary><c>runtime if runtime else "framework"</c>.</summary>
    public static string RuntimeFolder(string? runtime) => string.IsNullOrEmpty(runtime) ? "framework" : runtime;

    public static int Run(ReleaseOptions options, string root, string cwd, IReleaseSteps steps, TextWriter stdout)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(steps);
        if (!TextModeFile.SamePath(cwd, root))
        {
            throw new CommandExitException($"Run this script from the repository root: {root}");
        }

        steps.CleanArtifacts();
        steps.RunTests(options.SkipTests);
        steps.BuildCli(options.Runtime, !options.FrameworkDependent);
        steps.BuildGui(options.Runtime, !options.FrameworkDependent);
        if (!options.NoInstaller)
        {
            if (string.IsNullOrEmpty(options.ReleaseNotes))
            {
                throw new CommandExitException("--release-notes is required to build the installer. Use --no-installer to skip.");
            }

            steps.BuildInstaller(options.InstallerRid, options.ReleaseNotes, options.Channel, options.PackId);
        }

        var staged = options.Runtime is null || options.Runtime.StartsWith("win-", StringComparison.Ordinal);
        if (staged)
        {
            steps.StageLocalPortableDev(options.Runtime ?? "win-x64");
        }

        ConsoleText.Print(stdout, "\nRelease artifacts ready:");
        ConsoleText.Print(stdout, $" - CLI publish: {Path.Combine(CliArtifactDir(root), RuntimeFolder(options.Runtime))}");
        ConsoleText.Print(stdout, $" - GUI publish: {Path.Combine(GuiArtifactDir(root), RuntimeFolder(options.Runtime))}");
        if (staged)
        {
            ConsoleText.Print(stdout, $" - Local staged portable run dir: {StagePortable.DefaultStageDir}");
        }

        if (!options.NoInstaller)
        {
            ConsoleText.Print(stdout, $" - Installer: artifacts/velopack/releases/{options.InstallerRid}");
        }

        return 0;
    }
}
