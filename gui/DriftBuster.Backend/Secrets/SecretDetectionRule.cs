using DriftBuster.Backend.Infrastructure.PythonRe;

namespace DriftBuster.Backend.Secrets;

/// <summary><c>driftbuster.secret_scanning.SecretDetectionRule</c>.</summary>
public sealed record SecretDetectionRule(string Name, PythonPattern Pattern, string? Description = null);
