using System.Runtime.InteropServices;

using Microsoft.Win32.SafeHandles;

namespace DriftBuster.Backend.Registry;

public sealed partial class WinRegistryBackend
{
    [LibraryImport("advapi32.dll", EntryPoint = "RegOpenKeyExW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int RegOpenKeyEx(nint key, string subKey, int options, int access, out SafeRegistryHandle result);

    [LibraryImport("advapi32.dll", EntryPoint = "RegEnumKeyExW")]
    private static unsafe partial int RegEnumKeyEx(
        SafeRegistryHandle key,
        int index,
        char* name,
        int* nameLength,
        nint reserved,
        char* className,
        int* classLength,
        nint lastWriteTime);

    [LibraryImport("advapi32.dll", EntryPoint = "RegQueryInfoKeyW")]
    private static unsafe partial int RegQueryInfoKey(
        SafeRegistryHandle key,
        char* className,
        int* classLength,
        nint reserved,
        int* subKeys,
        int* maxSubKeyLength,
        int* maxClassLength,
        int* values,
        int* maxValueNameLength,
        int* maxValueLength,
        int* securityDescriptorLength,
        nint lastWriteTime);

    [LibraryImport("advapi32.dll", EntryPoint = "RegEnumValueW")]
    private static unsafe partial int RegEnumValue(
        SafeRegistryHandle key,
        int index,
        char* valueName,
        int* valueNameLength,
        nint reserved,
        int* type,
        byte* data,
        int* dataLength);
}
