using System.Text.RegularExpressions;

namespace DriftBuster.Backend.Secrets;

/// <summary>One named secret detection pattern.</summary>
public sealed record SecretDetectionRule(string Name, Regex Pattern, string? Description = null);
