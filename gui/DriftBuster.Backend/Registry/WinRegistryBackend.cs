using System.Runtime.Versioning;

using DriftBuster.Backend.Infrastructure;

using Microsoft.Win32.SafeHandles;

namespace DriftBuster.Backend.Registry;

/// <summary>
/// Registry reads through advapi32: <c>RegOpenKeyExW</c> with <c>KEY_READ</c> (plus <c>KEY_WOW64_64KEY</c>/<c>KEY_WOW64_32KEY</c> for
/// views "64"/"32"); a key that fails to open lists nothing. Subkeys via <c>RegEnumKeyExW</c>, values via <c>RegQueryInfoKeyW</c> and
/// <c>RegEnumValueW</c> (buffer doubled on <c>ERROR_MORE_DATA</c>); each list ends at the first failing call.
/// </summary>
/// <remarks>
/// Raw calls instead of <see cref="Microsoft.Win32.RegistryKey"/>, whose name normalisation, length checks and value conversion
/// (signed DWORD, empty MULTI_SZ strings dropped) would change what the scan reports.
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

        for (var index = 0; EnumValue(handle, index) is { } raw; index++)
        {
            results.Add(new KeyValuePair<string, object?>(raw.Name, RegistryValueDecoder.Convert(raw.Data, raw.Type)));
        }

        return results;
    }

    /// <summary>The key's values as stored: name, registry type and data bytes, in the key's order. Empty where the key cannot be opened.</summary>
    internal static IReadOnlyList<RegistryRawValue> EnumRawValues(string hive, string path, string? view)
    {
        using var handle = Open(hive, path, view);
        var results = new List<RegistryRawValue>();
        if (handle is null)
        {
            return results;
        }

        for (var index = 0; EnumValue(handle, index) is { } raw; index++)
        {
            results.Add(raw);
        }

        return results;
    }

    /// <summary>True when <paramref name="path"/> opens for reading under <paramref name="hive"/> in <paramref name="view"/>.</summary>
    public static bool KeyExists(string hive, string path, string? view)
    {
        using var handle = Open(hive, path, view);
        return handle is not null;
    }

    // Null when the key cannot be opened; a path holding NUL throws ArgumentException.
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

    // Null when the call fails.
    private static unsafe string? EnumKey(SafeRegistryHandle handle, int index)
    {
        var buffer = stackalloc char[KeyNameBufferLength];
        var length = KeyNameBufferLength;
        var rc = RegEnumKeyEx(handle, index, buffer, &length, 0, null, null, 0);
        return rc == 0 ? new string(buffer, 0, length) : null;
    }

    // Null when the call fails.
    private static unsafe RegistryRawValue? EnumValue(SafeRegistryHandle handle, int index)
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

            // Double the buffer; an allocation the runtime cannot make throws OutOfMemoryException.
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
        return new RegistryRawValue(text, type, data.AsSpan(0, Math.Min(dataSize, data.Length)).ToArray());
    }
}
