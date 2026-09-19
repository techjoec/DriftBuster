using System.Runtime.InteropServices;
using System.Text;

namespace DriftBuster.Backend.Infrastructure;

/// <summary>
/// <c>mkdir(2)</c> with mode 0777: the kernel resolves the path as given (a <c>..</c> after a link steps to the target's parent)
/// and the failure is its errno. Linux only; null elsewhere, and callers fall back to managed creation.
/// </summary>
/// <remarks>Derived from the publicly documented mkdir(2) interface, not vendor source.</remarks>
internal static partial class UnixMkdir
{
    private const uint DirectoryMode = 0x1FF;

    private static volatile bool _unavailable = !OperatingSystem.IsLinux();

    [LibraryImport("libc", EntryPoint = "mkdir", SetLastError = true)]
    private static unsafe partial int MkdirNative(byte* path, uint mode);

    /// <summary>0 when created, else the errno; null without a <c>mkdir(2)</c> binding or when the path holds NUL or an unpaired surrogate.</summary>
    internal static unsafe int? MakeDirectory(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (_unavailable || path.Contains('\0', StringComparison.Ordinal) || EngineUtf8.HasUnpairedSurrogate(path))
        {
            return null;
        }

        var encoded = new byte[Encoding.UTF8.GetMaxByteCount(path.Length) + 1];
        Encoding.UTF8.GetBytes(path, encoded);
        try
        {
            fixed (byte* bytes = encoded)
            {
                return MkdirNative(bytes, DirectoryMode) == 0 ? 0 : Marshal.GetLastPInvokeError();
            }
        }
        catch (Exception exc) when (exc is DllNotFoundException or EntryPointNotFoundException)
        {
            _unavailable = true;
            return null;
        }
    }
}
