using DriftBuster.Backend.Secrets;

namespace DriftBuster.Backend.Profiles.Run;

/// <summary><c>run_profiles.ProfileRunResult</c>.</summary>
public sealed record ProfileRunResult(
    RunProfile Profile,
    string Timestamp,
    string OutputDir,
    IReadOnlyList<ProfileFile> Files,
    OrderedDictionary<string, object?>? Secrets = null)
{
    /// <summary>The redaction guards the run's secret filter fired (<see cref="SecretDetectionContext.RedactionGuards"/>); not part of <c>to_dict</c>.</summary>
    internal IReadOnlyList<SecretRedactionGuard> RedactionGuards { get; init; } = [];

    /// <summary>One summary per source in the order the run collected them (the baseline first); not part of <c>to_dict</c>.</summary>
    public IReadOnlyList<ProfileRunSource> Sources { get; init; } = [];

    /// <summary><c>ProfileRunResult.to_dict()</c>: <c>secrets</c> only when it is not <c>None</c>.</summary>
    public OrderedDictionary<string, object?> ToDict()
    {
        var payload = new OrderedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["profile"] = Profile.ToDict(),
            ["timestamp"] = Timestamp,
            ["output_dir"] = OutputDir,
            ["files"] = Files.Select(entry => (object?)entry.ToDict()).ToList(),
        };
        if (Secrets is not null)
        {
            payload["secrets"] = Secrets;
        }

        return payload;
    }
}
