using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Registry;

/// <summary>The hive handles and access masks <c>_WinRegBackend._open</c> passes to <c>winreg.OpenKeyEx</c>.</summary>
public static class WinRegistryKeys
{
    /// <summary><c>winreg.KEY_READ</c>.</summary>
    public const int KeyRead = 0x20019;

    /// <summary><c>winreg.KEY_WOW64_64KEY</c>.</summary>
    public const int KeyWow64With64Key = 0x0100;

    /// <summary><c>winreg.KEY_WOW64_32KEY</c>.</summary>
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

    /// <summary><c>self._hives[hive]</c>: the handle for <c>HKLM</c> or <c>HKCU</c>; any other name raises Python's <c>KeyError</c>.</summary>
    /// <exception cref="KeyNotFoundException">The hive is neither, with the <c>KeyError</c> text (the name's repr).</exception>
    public static nint HiveHandle(string hive) => hive switch
    {
        "HKLM" => LocalMachine,
        "HKCU" => CurrentUser,
        _ => throw new KeyNotFoundException(PythonRepr.StrRepr(hive)),
    };
}
