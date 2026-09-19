namespace DriftBuster.Backend.Remote;

/// <summary>What a capture scans and how it records it.</summary>
public sealed record CaptureRunOptions
{
    public string Root { get; init; } = ".";

    /// <summary>A detection profile store file whose configs are matched against each detection.</summary>
    public string? Profiles { get; init; }

    public IReadOnlyList<string> ProfileTags { get; init; } = [];

    public string Glob { get; init; } = "**/*";

    public string HuntGlob { get; init; } = "**/*";

    public IReadOnlyList<string> HuntExclude { get; init; } = [];

    public bool SkipHunt { get; init; }

    public int SampleSize { get; init; } = 128 * 1024;

    public string OutputDir { get; init; } = "captures";

    public string? CaptureId { get; init; }

    public string? Operator { get; init; }

    public string? Environment { get; init; }

    public string? Reason { get; init; }

    /// <summary>Tokens replaced by <see cref="Placeholder"/> in every string of the snapshot.</summary>
    public IReadOnlyList<string> MaskTokens { get; init; } = [];

    public string Placeholder { get; init; } = Diff.RedactionFilter.DefaultPlaceholder;

    public bool AllowUnmasked { get; init; }

    /// <summary>Registry scan output files summarised in the manifest.</summary>
    public IReadOnlyList<string> RegistryScan { get; init; } = [];
}
