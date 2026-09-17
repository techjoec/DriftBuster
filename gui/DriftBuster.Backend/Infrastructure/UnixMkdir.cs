using System.Runtime.InteropServices;
using System.Text;

namespace DriftBuster.Backend.Infrastructure;

/// <summary>
/// <c>os.mkdir(path, 0o777)</c> over <c>mkdir(2)</c>: the kernel resolves every part of the path as given (a <c>..</c> after a link
/// steps to the parent of the link's target), and the failure is the call's own <c>errno</c>. Linux only, like the other byte-level
/// calls (<see cref="UnixFileType"/>, <see cref="UnixPathWalk"/>); <see cref="MakeDirectory"/> returns null elsewhere and where the
/// call cannot be made, and callers fall back to managed directory creation.
/// </summary>
/// <remarks>Derived from the publicly documented mkdir(2) interface, not vendor source.</remarks>
internal static partial class UnixMkdir
{
    private const uint DirectoryMode = 0x1FF;

    private static volatile bool _unavailable = !OperatingSystem.IsLinux();

    [LibraryImport("libc", EntryPoint = "mkdir", SetLastError = true)]
    private static unsafe partial int MkdirNative(byte* path, uint mode);

    /// <summary>
    /// 0 when <paramref name="path"/> was created, otherwise the <c>errno</c> of the failed call; null when this platform offers no
    /// <c>mkdir(2)</c> binding, or <paramref name="path"/> holds a NUL or an unpaired surrogate (text the kernel cannot receive as
    /// Python spells it: Python raises <c>ValueError</c> or <c>UnicodeEncodeError</c> before the call).
    /// </summary>
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
