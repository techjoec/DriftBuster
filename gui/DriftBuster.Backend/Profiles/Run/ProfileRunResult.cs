using DriftBuster.Backend.Secrets;

namespace DriftBuster.Backend.Profiles.Run;

/// <summary>The outcome of one profile run.</summary>
public sealed record ProfileRunResult(
    RunProfile Profile,
    string Timestamp,
    string OutputDir,
    IReadOnlyList<ProfileFile> Files,
    OrderedDictionary<string, object?>? Secrets = null)
{
    /// <summary>Redaction guards the run's secret filter fired (<see cref="SecretDetectionContext.RedactionGuards"/>); not serialised.</summary>
    internal IReadOnlyList<SecretRedactionGuard> RedactionGuards { get; init; } = [];

    /// <summary>Per-source summaries in collection order (baseline first); not serialised.</summary>
    public IReadOnlyList<ProfileRunSource> Sources { get; init; } = [];

    /// <summary>The serialised result; <c>secrets</c> only when set.</summary>
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
