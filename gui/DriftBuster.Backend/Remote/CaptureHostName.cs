using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DriftBuster.Backend.Remote;

/// <summary>
/// The host name of the machine being captured. On Windows it comes from <c>GetComputerNameExW(ComputerNamePhysicalDnsHostname)</c>,
/// a wide-character call, so a computer name outside ASCII is exact; <see cref="Dns.GetHostName"/> there calls Winsock's ANSI
/// <c>gethostname</c> and decodes its bytes, which changes such a name. Elsewhere <see cref="Dns.GetHostName"/> is used.
/// </summary>
internal static partial class CaptureHostName
{
    private const int ComputerNamePhysicalDnsHostname = 5;
    private const int ErrorMoreData = 234;

    /// <summary>The host name, domain part included where the platform reports it.</summary>
    /// <exception cref="Win32Exception">Windows refused the name.</exception>
    public static string Get() => OperatingSystem.IsWindows() ? PhysicalDnsHostName() : Dns.GetHostName();

    [SupportedOSPlatform("windows")]
    private static unsafe string PhysicalDnsHostName()
    {
        uint size = 0;
        if (!GetComputerNameEx(ComputerNamePhysicalDnsHostname, null, &size) && Marshal.GetLastPInvokeError() != ErrorMoreData)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        var buffer = new char[Math.Max(size, 1)];
        fixed (char* start = buffer)
        {
            if (!GetComputerNameEx(ComputerNamePhysicalDnsHostname, start, &size))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            return new string(start, 0, checked((int)size));
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetComputerNameExW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool GetComputerNameEx(int nameType, char* buffer, uint* size);
}
