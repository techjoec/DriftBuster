namespace DriftBuster.Backend.Remote;

/// <summary>
/// The <c>capture.py run</c> arguments (<c>argparse.Namespace</c>), each defaulting as the parser defaults it. Paths are used as given:
/// relative ones resolve against the working directory.
/// </summary>
public sealed record CaptureRunOptions
{
    /// <summary><c>root</c>: the file or directory to scan.</summary>
    public string Root { get; init; } = ".";

    /// <summary><c>--profiles</c>: a detection profile store JSON payload; null or empty scans without profiles.</summary>
    public string? Profiles { get; init; }

    /// <summary><c>--profile-tag</c> (repeatable).</summary>
    public IReadOnlyList<string> ProfileTags { get; init; } = [];

    /// <summary><c>--glob</c> for the detection walk.</summary>
    public string Glob { get; init; } = "**/*";

    /// <summary><c>--hunt-glob</c> for the hunt walk.</summary>
    public string HuntGlob { get; init; } = "**/*";

    /// <summary><c>--hunt-exclude</c> (repeatable).</summary>
    public IReadOnlyList<string> HuntExclude { get; init; } = [];

    /// <summary><c>--skip-hunt</c>.</summary>
    public bool SkipHunt { get; init; }

    /// <summary><c>--sample-size</c> in bytes, for detection (clamped by the detector) and hunt (as given).</summary>
    public long SampleSize { get; init; } = 128 * 1024;

    /// <summary><c>--output-dir</c>.</summary>
    public string OutputDir { get; init; } = "captures";

    /// <summary><c>--capture-id</c>; null or empty uses the UTC time as <c>%Y%m%dT%H%M%SZ</c>.</summary>
    public string? CaptureId { get; init; }

    /// <summary><c>--operator</c>.</summary>
    public string? Operator { get; init; }

    /// <summary><c>--environment</c>.</summary>
    public string? Environment { get; init; }

    /// <summary><c>--reason</c>.</summary>
    public string? Reason { get; init; }

    /// <summary><c>--mask-token</c> (repeatable).</summary>
    public IReadOnlyList<string> MaskTokens { get; init; } = [];

    /// <summary><c>--placeholder</c>.</summary>
    public string Placeholder { get; init; } = "[REDACTED]";

    /// <summary><c>--allow-unmasked</c>.</summary>
    public bool AllowUnmasked { get; init; }

    /// <summary><c>--registry-scan</c> (repeatable): <c>registry_scan.json</c> files summarised into the manifest.</summary>
    public IReadOnlyList<string> RegistryScan { get; init; } = [];
}
