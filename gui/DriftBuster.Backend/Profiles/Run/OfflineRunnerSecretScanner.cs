using System.Text.Json;

namespace DriftBuster.Backend.Profiles.Run;

public sealed record OfflineRunnerSecretScanner(IReadOnlyList<string> IgnoreRules, IReadOnlyList<string> IgnorePatterns, JsonElement Ruleset);
