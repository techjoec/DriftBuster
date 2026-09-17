namespace DriftBuster.Cli.Commands;

/// <summary>The arguments of <c>driftbuster release stage-portable</c>.</summary>
internal sealed record StagePortableOptions
{
    public string StageDir { get; init; } = StagePortable.DefaultStageDir;

    public string Rid { get; init; } = "win-x64";

    public string Configuration { get; init; } = "Release";

    public string? Timestamp { get; init; }
}
