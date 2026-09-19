namespace DriftBuster.Backend.Secrets;

/// <summary>One rule as written: a .NET regular expression, <see cref="Flags"/> <c>i</c> for a case-insensitive match.</summary>
public sealed record SecretRuleDefinition(string Name, string? Description, string Pattern, string Flags = "");
