namespace DriftBuster.Backend.MultiServer;

/// <summary>
/// The registry part of a host's scan: key entries (a <c>HKLM\…</c> or <c>HKCU\…</c> key, or an application name), the
/// computer (null for this machine) and an optional credential file for it.
/// </summary>
public sealed record MultiServerRegistry(IReadOnlyList<string> Keys, string? Computer, string? CredentialFile)
{
    /// <summary>Null when there are no keys to read.</summary>
    internal static MultiServerRegistry? Create(IEnumerable<string?>? keys, string? computer, string? credentialFile)
    {
        var entries = (keys ?? []).Select(key => key?.Trim() ?? string.Empty).Where(key => key.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return entries.Count == 0
            ? null
            : new MultiServerRegistry(entries, string.IsNullOrWhiteSpace(computer) ? null : computer.Trim(), string.IsNullOrWhiteSpace(credentialFile) ? null : credentialFile.Trim());
    }
}
