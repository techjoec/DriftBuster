using DriftBuster.Backend.Infrastructure.EngineRe;

namespace DriftBuster.Backend.Secrets;

/// <summary>One named secret detection pattern.</summary>
public sealed record SecretDetectionRule(string Name, EnginePattern Pattern, string? Description = null);
