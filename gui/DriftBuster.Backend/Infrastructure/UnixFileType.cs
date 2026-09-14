using System.Runtime.InteropServices;
using System.Text;

namespace DriftBuster.Backend.Infrastructure;

/// <summary>
/// The file type <c>stat</c> reports for a path, read without opening the file. Linux only, through <c>statx(2)</c>, whose
/// structure has one layout on every architecture; <see cref="Stat"/> returns null elsewhere (and where the call itself is
/// unavailable), and callers fall back to managed checks.
/// </summary>
/// <remarks>Derived from the publicly documented statx(2) interface, not vendor source.</remarks>
internal static partial class UnixFileType
{
    private const int AtFdCwd = -100;
    private const int AtSymlinkNoFollow = 0x100;
    private const uint StatxTypeMask = 0x1;
    private const int StatxBufferSize = 256;
    private const int StatxModeOffset = 28;
    private const int FileTypeMask = 0xF000;
    private const int RegularFileType = 0x8000;
    private const int DirectoryFileType = 0x4000;
    private const int NoSuchSystemCall = 38;
    private const int OperationNotPermitted = 1;
    private const int PermissionDenied = 13;

    // pathlib._abc._IGNORED_ERRNOS: ENOENT, ENOTDIR, EBADF, ELOOP. Path.is_file() reports these as False and raises any other.
    private static readonly int[] IgnoredErrors = [2, 20, 9, 40];

    private static volatile bool _unavailable = !OperatingSystem.IsLinux();

    /// <summary>What a successful <c>stat</c> says the path is, or <see cref="Missing"/> when it fails with an error Python ignores.</summary>
    internal enum Kind
    {
        /// <summary>
        /// The call failed with <c>ENOENT</c>, <c>ENOTDIR</c>, <c>EBADF</c> or <c>ELOOP</c>: no such entry, a component that is not a
        /// directory, a dangling or looping link.
        /// </summary>
        Missing,

        /// <summary><c>S_ISREG</c>.</summary>
        Regular,

        /// <summary><c>S_ISDIR</c>.</summary>
        Directory,

        /// <summary>Anything else: a FIFO, a socket, a character or block device, or (not following links) a symlink.</summary>
        Other,
    }

    [LibraryImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static unsafe partial int Statx(int directoryFd, byte* path, int flags, uint mask, byte* buffer);

    /// <summary>
    /// <c>os.stat(path, follow_symlinks=...)</c> reduced to its file type; null when this platform or kernel offers no
    /// <c>statx</c>. A path holding a NUL character is <see cref="Kind.Missing"/> (Python raises <c>ValueError</c>, which
    /// <c>is_file()</c> reports as False).
    /// </summary>
    /// <exception cref="UnauthorizedAccessException">The lookup was refused (<c>EACCES</c>: a component cannot be searched).</exception>
    /// <exception cref="IOException">Any other failure Python's <c>is_file()</c> raises (<c>ENAMETOOLONG</c>, <c>EIO</c>, ...).</exception>
    internal static Kind? Stat(string path, bool followSymlinks)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (_unavailable)
        {
            return null;
        }

        if (path.Contains('\0', StringComparison.Ordinal))
        {
            return Kind.Missing;
        }

        var encoded = new byte[Encoding.UTF8.GetMaxByteCount(path.Length) + 1];
        Encoding.UTF8.GetBytes(path, encoded);
        return Stat(encoded, followSymlinks, path);
    }

    /// <summary>
    /// <see cref="Stat(string, bool)"/> over the bytes the kernel receives, for a path holding a name that is not UTF-8.
    /// <paramref name="path"/> must end with a NUL byte and hold no other; <paramref name="display"/> names the path in an error.
    /// </summary>
    internal static unsafe Kind? Stat(ReadOnlySpan<byte> path, bool followSymlinks, string display)
    {
        if (_unavailable)
        {
            return null;
        }

        var buffer = stackalloc byte[StatxBufferSize];
        int result;
        try
        {
            fixed (byte* pathBytes = path)
            {
                result = Statx(AtFdCwd, pathBytes, followSymlinks ? 0 : AtSymlinkNoFollow, StatxTypeMask, buffer);
            }
        }
        catch (Exception exc) when (exc is DllNotFoundException or EntryPointNotFoundException)
        {
            _unavailable = true;
            return null;
        }

        if (result != 0)
        {
            // ENOSYS: a kernel before 4.11; EPERM: a seccomp filter that predates statx. Neither is an answer about the path.
            var error = Marshal.GetLastPInvokeError();
            if (error is NoSuchSystemCall or OperationNotPermitted)
            {
                _unavailable = true;
                return null;
            }

            if (Array.IndexOf(IgnoredErrors, error) >= 0)
            {
                return Kind.Missing;
            }

            var message = $"[Errno {error}] {Marshal.GetPInvokeErrorMessage(error)}: '{display}'";
            throw error == PermissionDenied ? new UnauthorizedAccessException(message) : new IOException(message);
        }

        // stx_mode is a native-endian __u16.
        return (System.Runtime.CompilerServices.Unsafe.ReadUnaligned<ushort>(buffer + StatxModeOffset) & FileTypeMask) switch
        {
            RegularFileType => Kind.Regular,
            DirectoryFileType => Kind.Directory,
            _ => Kind.Other,
        };
    }
}
