namespace DriftBuster.Backend.Curation;

/// <summary>One ignore, mask or unmask choice, for every run or for one host set (<see cref="Scope"/>).</summary>
public sealed record CurationChoice
{
    public CurationTarget Target { get; init => field = value ?? new(); } = new();

    /// <summary>One of <see cref="CurationChoiceKinds"/>.</summary>
    public string Kind { get; init => field = value ?? CurationChoiceKinds.Ignore; } = CurationChoiceKinds.Ignore;

    /// <summary>Empty for every run, otherwise the host set id (<see cref="CurationScopes.HostSetId"/>) it applies to.</summary>
    public string Scope { get; init => field = value ?? string.Empty; } = string.Empty;

    public string Note { get; init => field = value ?? string.Empty; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; }
}
