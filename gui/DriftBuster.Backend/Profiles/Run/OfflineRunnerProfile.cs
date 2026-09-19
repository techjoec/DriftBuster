using DriftBuster.Backend.Models;

namespace DriftBuster.Backend.Profiles.Run;

/// <summary>The profile part of an offline runner config; the packaged secret ruleset travels in <see cref="SecretScanner"/>.</summary>
public sealed record OfflineRunnerProfile(
    string Name,
    string? Description,
    string? Baseline,
    IReadOnlyList<RunProfileSource> Sources,
    IReadOnlyList<string> Tags,
    IReadOnlyDictionary<string, string> Options,
    OfflineRunnerSecretScanner SecretScanner);
