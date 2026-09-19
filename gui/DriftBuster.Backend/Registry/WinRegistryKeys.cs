using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Registry;

/// <summary>Hive handles and access masks for opening registry keys.</summary>
public static class WinRegistryKeys
{
    public const int KeyRead = 0x20019;

    public const int KeyWow64With64Key = 0x0100;

    public const int KeyWow64With32Key = 0x0200;

    /// <summary><c>HKEY_LOCAL_MACHINE</c> as the sign-extended predefined handle.</summary>
    public static readonly nint LocalMachine = unchecked((int)0x80000002);

    /// <summary><c>HKEY_CURRENT_USER</c> as the sign-extended predefined handle.</summary>
    public static readonly nint CurrentUser = unchecked((int)0x80000001);

    /// <summary>The access mask for <paramref name="view"/>: <c>KEY_READ</c>, plus the WOW64 flag for "64" or "32".</summary>
    public static int AccessFor(string? view) => view switch
    {
        "64" => KeyRead | KeyWow64With64Key,
        "32" => KeyRead | KeyWow64With32Key,
        _ => KeyRead,
    };

    /// <summary>The handle for <c>HKLM</c> or <c>HKCU</c>.</summary>
    /// <exception cref="KeyNotFoundException">The hive is neither.</exception>
    public static nint HiveHandle(string hive) => hive switch
    {
        "HKLM" => LocalMachine,
        "HKCU" => CurrentUser,
        _ => throw new KeyNotFoundException($"Unknown registry hive {EngineRepr.StrRepr(hive)}."),
    };
}
