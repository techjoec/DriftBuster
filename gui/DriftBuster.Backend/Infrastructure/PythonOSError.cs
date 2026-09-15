using System.Globalization;
using System.Runtime.InteropServices;

namespace DriftBuster.Backend.Infrastructure;

/// <summary><c>str(OSError(errno, strerror, filename))</c>: <c>[Errno 13] Permission denied: '/path'</c>.</summary>
public static class PythonOSError
{
    /// <summary><c>ENOENT</c>.</summary>
    public const int NoSuchFile = 2;

    /// <summary><c>EPERM</c>.</summary>
    public const int OperationNotPermitted = 1;

    /// <summary><c>EACCES</c>.</summary>
    public const int PermissionDenied = 13;

    /// <summary><c>EEXIST</c>.</summary>
    public const int FileExists = 17;

    /// <summary><c>ENOTDIR</c>.</summary>
    public const int NotADirectory = 20;

    /// <summary><c>EISDIR</c>.</summary>
    public const int IsADirectory = 21;

    /// <summary><c>EINVAL</c>.</summary>
    public const int InvalidArgument = 22;

    /// <summary><c>ENAMETOOLONG</c>.</summary>
    public const int NameTooLong = 36;

    /// <summary><c>ELOOP</c>.</summary>
    public const int TooManyLinks = 40;

    /// <summary>An <see cref="IOException"/> whose message is Python's text for <paramref name="errno"/> without a file name.</summary>
    public static IOException Create(int errno)
        => new(string.Create(CultureInfo.InvariantCulture, $"[Errno {errno}] {Marshal.GetPInvokeErrorMessage(errno)}")) { HResult = errno };

    /// <summary>An <see cref="IOException"/> whose message is Python's text for <paramref name="errno"/> on <paramref name="filename"/>.</summary>
    public static IOException Create(int errno, string filename, Exception? inner = null)
    {
        ArgumentNullException.ThrowIfNull(filename);
        var message = string.Create(
            CultureInfo.InvariantCulture,
            $"[Errno {errno}] {Marshal.GetPInvokeErrorMessage(errno)}: {PythonRepr.StrRepr(filename)}");
        return new IOException(message, inner) { HResult = errno };
    }

    /// <summary>
    /// The <c>OSError</c> subclass CPython raises for <paramref name="errno"/> (the ones a file-system call on Linux gives; any other is
    /// <c>OSError</c>).
    /// </summary>
    public static string TypeName(int errno) => errno switch
    {
        NoSuchFile => "FileNotFoundError",
        OperationNotPermitted or PermissionDenied => "PermissionError",
        FileExists => "FileExistsError",
        NotADirectory => "NotADirectoryError",
        IsADirectory => "IsADirectoryError",
        _ => "OSError",
    };

    /// <summary>
    /// The <c>errno</c> behind a runtime I/O exception: the raw error the runtime keeps on Unix (on the exception itself or, for
    /// <see cref="UnauthorizedAccessException"/>, its inner exception), <c>ENOENT</c> for a missing file or directory,
    /// <c>ENAMETOOLONG</c> for a path that is too long, and <c>EACCES</c> for any other refusal; null when none is known.
    /// </summary>
    public static int? Errno(Exception exc)
    {
        ArgumentNullException.ThrowIfNull(exc);
        static bool IsRaw(int value) => value is > 0 and < 4096;
        return exc switch
        {
            FileNotFoundException or DirectoryNotFoundException => NoSuchFile,
            PathTooLongException => NameTooLong,
            UnauthorizedAccessException { InnerException: { } inner } when IsRaw(inner.HResult) => inner.HResult,
            UnauthorizedAccessException => PermissionDenied,
            IOException when IsRaw(exc.HResult) => exc.HResult,
            _ => null,
        };
    }
}
