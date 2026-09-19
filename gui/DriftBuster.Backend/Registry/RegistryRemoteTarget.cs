using System.Text.Json.Serialization;

namespace DriftBuster.Backend.Registry;

/// <summary>A host the runner scans over WinRM: <c>host[,port=N][,use-ssl=yes|no][,username=U][,password-env=VAR][,credential-profile=P][,transport=T][,alias=A]</c>.</summary>
public sealed record RegistryRemoteTarget
{
    public required string Host { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Port { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? UseSsl { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Username { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PasswordEnv { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CredentialProfile { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Transport { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Alias { get; init; }

    /// <summary>The comma-separated form; an unknown key, an empty value or a bad port or switch raises <see cref="FormatException"/>.</summary>
    public static RegistryRemoteTarget Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var parts = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts[0].Contains('=', StringComparison.Ordinal))
        {
            throw new FormatException("A remote target starts with the host name.");
        }

        var target = new RegistryRemoteTarget { Host = parts[0] };
        foreach (var entry in parts.Skip(1))
        {
            var separator = entry.IndexOf('=', StringComparison.Ordinal);
            var key = separator < 0 ? entry : entry[..separator].Trim().ToLowerInvariant().Replace('-', '_');
            var text = separator < 0 ? string.Empty : entry[(separator + 1)..].Trim();
            if (text.Length == 0)
            {
                throw new FormatException($"Remote target entry '{entry}' needs a value.");
            }

            target = key switch
            {
                "port" => target with { Port = int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var port) && port is > 0 and < 65536 ? port : throw new FormatException($"Invalid port '{text}'.") },
                "use_ssl" => target with { UseSsl = text.ToLowerInvariant() switch { "1" or "true" or "yes" or "on" => true, "0" or "false" or "no" or "off" => false, _ => throw new FormatException($"Invalid use-ssl value '{text}'.") } },
                "username" or "user" => target with { Username = text },
                "password_env" => target with { PasswordEnv = text },
                "credential_profile" => target with { CredentialProfile = text },
                "transport" => target with { Transport = text },
                "alias" => target with { Alias = text },
                _ => throw new FormatException($"Unsupported remote target key '{key}'."),
            };
        }

        return target;
    }
}
