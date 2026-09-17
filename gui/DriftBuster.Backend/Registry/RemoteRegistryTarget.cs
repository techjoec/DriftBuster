using System.Numerics;

using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Registry;

/// <summary>
/// <c>offline_runner.RemoteRegistryTarget</c>: a remote host a registry scan runs against, with its transport (default
/// <c>winrm</c>), port, SSL flag and the credential references (never a password). Values are in the <see cref="EngineJson"/>
/// domain.
/// </summary>
public sealed record RemoteRegistryTarget(
    string Host,
    string Transport = "winrm",
    BigInteger? Port = null,
    bool? UseSsl = null,
    string? Username = null,
    string? PasswordEnv = null,
    string? CredentialProfile = null,
    string? Alias = null)
{
    private static readonly string[] TrueWords = ["1", "true", "yes", "on"];
    private static readonly string[] FalseWords = ["0", "false", "no", "off"];

    /// <summary>
    /// <c>RemoteRegistryTarget._coerce_bool(value)</c>: a bool as itself, otherwise <c>str(value).strip().lower()</c> as one of
    /// 1/true/yes/on or 0/false/no/off.
    /// </summary>
    internal static bool CoerceBool(object? value)
    {
        if (value is bool flag)
        {
            return flag;
        }

        var text = EngineText.Lower(EngineText.Strip(EngineRepr.Str(value)));
        if (TrueWords.Contains(text, StringComparer.Ordinal))
        {
            return true;
        }

        return FalseWords.Contains(text, StringComparer.Ordinal)
            ? false
            : throw new EngineValueException($"Unsupported boolean value '{EngineRepr.Str(value)}' for remote target", nameof(value));
    }

    /// <summary>
    /// <c>RemoteRegistryTarget.from_payload(payload)</c>: a str is the stripped host; a mapping needs a truthy <c>host</c> (or
    /// <c>hostname</c>) and no <c>password</c> key, and reads <c>password_env</c>/<c>password-env</c>, <c>username</c>/<c>user</c>,
    /// <c>credential_profile</c>/<c>credential-profile</c>, <c>transport</c> (lower-cased, <c>winrm</c> when falsy), a positive
    /// <c>int(port)</c>, <c>use_ssl</c>/<c>use-ssl</c> through <see cref="CoerceBool"/> and <c>alias</c>, each text stripped and
    /// dropped when blank.
    /// </summary>
    /// <exception cref="EngineValueException">Python's <c>ValueError</c> text for each refusal, or from <c>int()</c>.</exception>
    public static RemoteRegistryTarget FromPayload(object? payload)
    {
        if (payload is string hostText)
        {
            var host = EngineText.Strip(hostText);
            return host.Length == 0
                ? throw new EngineValueException("remote target host must be non-empty", nameof(payload))
                : new RemoteRegistryTarget(host);
        }

        if (payload is not IReadOnlyDictionary<string, object?> mapping)
        {
            throw new EngineValueException("remote target must be a string host or mapping", nameof(payload));
        }

        var hostValue = Or(mapping, "host", "hostname");
        if (!EngineBuiltins.IsTruthy(hostValue) || EngineText.Strip(EngineRepr.Str(hostValue)).Length == 0)
        {
            throw new EngineValueException("remote target requires 'host'", nameof(payload));
        }

        if (mapping.ContainsKey("password"))
        {
            throw new EngineValueException("remote target must not embed raw passwords; use password_env", nameof(payload));
        }

        var passwordEnv = FirstPresent(mapping, "password_env", "password-env");
        if (passwordEnv is not null && EngineText.Strip(EngineRepr.Str(passwordEnv)).Length == 0)
        {
            throw new EngineValueException("remote target password_env must be non-empty when provided", nameof(payload));
        }

        var username = Or(mapping, "username", "user");
        var credentialProfile = FirstPresent(mapping, "credential_profile", "credential-profile");
        var transportValue = mapping.TryGetValue("transport", out var transportRaw) ? transportRaw : "winrm";
        var transport = EngineBuiltins.IsTruthy(transportValue) ? EngineText.Lower(EngineText.Strip(EngineRepr.Str(transportValue))) : "winrm";
        var aliasValue = mapping.GetValueOrDefault("alias");
        var port = ReadPort(mapping.GetValueOrDefault("port"));
        var useSslValue = FirstPresent(mapping, "use_ssl", "use-ssl");
        bool? useSsl = useSslValue is not null ? CoerceBool(useSslValue) : null;

        return new RemoteRegistryTarget(
            EngineText.Strip(EngineRepr.Str(hostValue)),
            transport,
            port,
            useSsl,
            OptionalText(username),
            OptionalText(passwordEnv),
            OptionalText(credentialProfile),
            OptionalText(aliasValue));
    }

    // payload.get(first) or payload.get(second)
    private static object? Or(IReadOnlyDictionary<string, object?> mapping, string first, string second)
    {
        var value = mapping.GetValueOrDefault(first);
        return EngineBuiltins.IsTruthy(value) ? value : mapping.GetValueOrDefault(second);
    }

    // payload[first] if first in payload, else payload[second] if second in payload, else None.
    private static object? FirstPresent(IReadOnlyDictionary<string, object?> mapping, string first, string second)
        => mapping.TryGetValue(first, out var value) ? value : mapping.GetValueOrDefault(second);

    // str(value).strip() if value and str(value).strip() else None.
    private static string? OptionalText(object? value)
    {
        if (!EngineBuiltins.IsTruthy(value))
        {
            return null;
        }

        var text = EngineText.Strip(EngineRepr.Str(value));
        return text.Length > 0 ? text : null;
    }

    private static BigInteger? ReadPort(object? value)
    {
        if (value is null)
        {
            return null;
        }

        var port = EngineBuiltins.Int(value);
        return port <= 0 ? throw new EngineValueException("remote target port must be positive", nameof(value)) : port;
    }
}
