namespace DriftBuster.Cli.Commands;

/// <summary>The arguments of <c>driftbuster release</c>.</summary>
internal sealed record ReleaseOptions
{
    public bool SkipTests { get; init; }

    public string? Runtime { get; init; }

    public bool FrameworkDependent { get; init; }

    public bool NoInstaller { get; init; }

    public string InstallerRid { get; init; } = "win-x64";

    public string? ReleaseNotes { get; init; }

    public string? Channel { get; init; }

    public string? PackId { get; init; }
}
