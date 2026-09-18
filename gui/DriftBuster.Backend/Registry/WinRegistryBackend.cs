using System.Runtime.Versioning;

using DriftBuster.Backend.Infrastructure;

using Microsoft.Win32.SafeHandles;

namespace DriftBuster.Backend.Registry;

/// <summary>
/// <c>registry.scan._WinRegBackend</c>: the calls <c>winreg</c> makes, in its order. A key is opened with <c>RegOpenKeyExW</c> and
/// <c>KEY_READ</c>, plus <c>KEY_WOW64_64KEY</c> for view "64" or <c>KEY_WOW64_32KEY</c> for view "32"; a key that fails to open lists
/// nothing. Subkeys are <c>RegEnumKeyExW</c> by index into a 257-unit buffer and values <c>RegQueryInfoKeyW</c> then
/// <c>RegEnumValueW</c> by index (the data buffer doubled on <c>ERROR_MORE_DATA</c>), each list ending at the first failing call. Value
/// data is converted by <see cref="RegistryValueDecoder"/>.
/// </summary>
/// <remarks>
/// The raw advapi32 calls are used instead of <see cref="Microsoft.Win32.RegistryKey"/>, whose name normalisation (collapsed and
/// trailing backslashes), name length checks and value conversion (signed <c>REG_DWORD</c>, empty <c>REG_MULTI_SZ</c> strings dropped)
/// would change the names and values the scan reports.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed partial class WinRegistryBackend : IRegistryBackend
{
    private const int ErrorMoreData = 234;
    private const int KeyNameBufferLength = 257;

    public IReadOnlyList<string> EnumSubkeys(string hive, string path, string? view)
    {
        using var handle = Open(hive, path, view);
        var results = new List<string>();
        if (handle is null)
        {
            return results;
        }

        for (var index = 0; EnumKey(handle, index) is { } name; index++)
        {
            results.Add(name);
        }

        return results;
    }

    public IReadOnlyList<KeyValuePair<string, object?>> EnumValues(string hive, string path, string? view)
    {
        using var handle = Open(hive, path, view);
        var results = new List<KeyValuePair<string, object?>>();
        if (handle is null)
        {
            return results;
        }

        for (var index = 0; EnumValue(handle, index) is { } pair; index++)
        {
            results.Add(pair);
        }

        return results;
    }

    // _open: None where the key cannot be opened. A path holding NUL raises ArgumentException.
    private static SafeRegistryHandle? Open(string hive, string path, string? view)
    {
        ArgumentNullException.ThrowIfNull(path);
        var root = WinRegistryKeys.HiveHandle(hive);
        if (path.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("Null character in path.", nameof(path));
        }

        var rc = RegOpenKeyEx(root, path, 0, WinRegistryKeys.AccessFor(view), out var handle);
        if (rc == 0)
        {
            return handle;
        }

        handle.Dispose();
        return null;
    }

    // winreg.EnumKey: None where the call fails.
    private static unsafe string? EnumKey(SafeRegistryHandle handle, int index)
    {
        var buffer = stackalloc char[KeyNameBufferLength];
        var length = KeyNameBufferLength;
        var rc = RegEnumKeyEx(handle, index, buffer, &length, 0, null, null, 0);
        return rc == 0 ? new string(buffer, 0, length) : null;
    }

    // winreg.EnumValue: None where the call fails.
    private static unsafe KeyValuePair<string, object?>? EnumValue(SafeRegistryHandle handle, int index)
    {
        int maxNameLength;
        int maxDataLength;
        if (RegQueryInfoKey(handle, null, null, 0, null, null, null, null, &maxNameLength, &maxDataLength, null, 0) != 0)
        {
            return null;
        }

        var bufValueSize = maxNameLength + 1;
        var bufDataSize = maxDataLength + 1;
        var name = new char[bufValueSize];
        var data = new byte[bufDataSize];
        var valueSize = bufValueSize;
        var dataSize = bufDataSize;
        int type;
        int rc;
        while (true)
        {
            fixed (char* namePointer = name)
            fixed (byte* dataPointer = data)
            {
                rc = RegEnumValue(handle, index, namePointer, &valueSize, 0, &type, dataPointer, &dataSize);
            }

            if (rc != ErrorMoreData)
            {
                break;
            }

            // PyMem_Realloc to twice the size: an allocation the runtime cannot make raises OutOfMemoryException.
            var grown = new byte[(long)bufDataSize * 2];
            data.CopyTo(grown, 0);
            data = grown;
            bufDataSize = grown.Length;
            dataSize = bufDataSize;
            valueSize = bufValueSize;
        }

        if (rc != 0)
        {
            return null;
        }

        var nameEnd = Array.IndexOf(name, '\0');
        var text = new string(name, 0, nameEnd < 0 ? name.Length : nameEnd);
        return new KeyValuePair<string, object?>(text, RegistryValueDecoder.Convert(data.AsSpan(0, Math.Min(dataSize, data.Length)), type));
    }
}
