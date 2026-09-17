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

    /// <summary>Linux <c>ENAMETOOLONG</c> (the MSVC value is 38; on Windows the runtime's exception carries the Win32 code instead).</summary>
    public const int NameTooLong = 36;

    /// <summary><c>ELOOP</c>.</summary>
    public const int TooManyLinks = 40;

    /// <summary>
    /// The errno Python's <c>open()</c> raises for a directory: <c>EISDIR</c> (<c>IsADirectoryError</c>) from the kernel on Unix; on
    /// Windows the CRT's <c>_wopen</c> fails with <c>ERROR_ACCESS_DENIED</c>, which it maps to <c>EACCES</c> (<c>PermissionError</c>).
    /// </summary>
    public static int DirectoryOpenErrno => OperatingSystem.IsWindows() ? PermissionDenied : IsADirectory;

    /// <summary>Linux <c>EFBIG</c> (the runtime reports it as <see cref="ArgumentOutOfRangeException"/> on Unix).</summary>
    public const int FileTooLarge = 27;

    /// <summary>
    /// An <see cref="IOException"/> whose message is Python's text for <paramref name="errno"/> without a file name: the error a read or
    /// write raises once the file is open.
    /// </summary>
    public static IOException Create(int errno, Exception? inner = null)
        => new(string.Create(CultureInfo.InvariantCulture, $"[Errno {errno}] {StrError(errno)}"), inner) { HResult = errno };

    /// <summary>
    /// The <c>ValueError</c> CPython raises before any system call for a path holding a NUL character: <c>open()</c>'s
    /// <c>embedded null byte</c> (<c>embedded null character</c> on Windows, where <c>FileIO</c> converts the name to wide characters)
    /// when <paramref name="function"/> is null, else the <c>os</c> function's <c>{function}: embedded null character in path</c>.
    /// </summary>
    /// <exception cref="PythonValueException">The path holds a NUL character.</exception>
    public static void ThrowIfEmbeddedNull(string path, string? function = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!path.Contains('\0', StringComparison.Ordinal))
        {
            return;
        }

        var message = function is not null
            ? $"{function}: embedded null character in path"
            : OperatingSystem.IsWindows() ? "embedded null character" : "embedded null byte";
        throw new PythonValueException(message, nameof(path));
    }

    /// <summary>An <see cref="IOException"/> whose message is Python's text for <paramref name="errno"/> on <paramref name="filename"/>.</summary>
    public static IOException Create(int errno, string filename, Exception? inner = null)
    {
        ArgumentNullException.ThrowIfNull(filename);
        var message = string.Create(CultureInfo.InvariantCulture, $"[Errno {errno}] {StrError(errno)}: {PythonRepr.StrRepr(filename)}");
        return new IOException(message, inner) { HResult = errno };
    }

    /// <summary>
    /// The message <c>PyErr_SetFromErrnoWithFilenameObjects</c> (<c>Python/errors.c</c>) puts after <c>[Errno N]</c>: the C library's
    /// <c>strerror</c> on Unix; on Windows the CRT's <c>_sys_errlist</c> entry for <c>0 &lt; errno &lt; _sys_nerr</c>
    /// (<see cref="WindowsStrError"/>), else <c>FormatMessage</c> of the value read as a Win32 code with trailing dots and white space removed.
    /// </summary>
    public static string StrError(int errno)
        => OperatingSystem.IsWindows()
            ? WindowsStrError(errno) ?? Marshal.GetPInvokeErrorMessage(errno).TrimEnd(TrailingMessageChars)
            : Marshal.GetPInvokeErrorMessage(errno);

    private static readonly char[] TrailingMessageChars = ['.', ' ', '\r', '\n', '\t'];

    // The MSVC CRT's _sys_errlist (index 0 is "No error", never used: errno 0 is spelled "Error" by CPython), read from CPython on Windows.
    private static readonly string[] WindowsErrorTable =
    [
        "No error", "Operation not permitted", "No such file or directory", "No such process", "Interrupted function call",
        "Input/output error", "No such device or address", "Arg list too long", "Exec format error", "Bad file descriptor",
        "No child processes", "Resource temporarily unavailable", "Not enough space", "Permission denied", "Bad address",
        "Unknown error", "Resource device", "File exists", "Improper link", "No such device",
        "Not a directory", "Is a directory", "Invalid argument", "Too many open files in system", "Too many open files",
        "Inappropriate I/O control operation", "Unknown error", "File too large", "No space left on device", "Invalid seek",
        "Read-only file system", "Too many links", "Broken pipe", "Domain error", "Result too large",
        "Unknown error", "Resource deadlock avoided", "Unknown error", "Filename too long", "No locks available",
        "Function not implemented", "Directory not empty", "Illegal byte sequence",
    ];

    /// <summary>The CRT's <c>_sys_errlist[errno]</c> for <c>0 &lt; errno &lt; _sys_nerr</c> (43), null outside that range.</summary>
    public static string? WindowsStrError(int errno) => errno > 0 && errno < WindowsErrorTable.Length ? WindowsErrorTable[errno] : null;

    /// <summary>
    /// The <c>OSError</c> subclass CPython raises for <paramref name="errno"/> on this platform: <c>Objects/exceptions.c</c>'s
    /// <c>errnomap</c> over the platform's <c>errno</c> values (see <see cref="TypeName(int, bool)"/>); any other is <c>OSError</c>.
    /// </summary>
    public static string TypeName(int errno) => TypeName(errno, OperatingSystem.IsWindows());

    /// <summary>
    /// <see cref="TypeName(int)"/> for the Linux <c>errno</c> values (the values the fake registry backends and the runtime's Unix exceptions
    /// carry), or for the values CPython on Windows registers: the MSVC ones shared with Linux up to <c>EPIPE</c>, <c>ETIMEDOUT</c> as 138,
    /// and the Winsock codes it substitutes for the connection errnos.
    /// </summary>
    public static string TypeName(int errno, bool windows) => errno switch
    {
        NoSuchFile => "FileNotFoundError",
        OperationNotPermitted or PermissionDenied => "PermissionError",
        FileExists => "FileExistsError",
        NotADirectory => "NotADirectoryError",
        IsADirectory => "IsADirectoryError",
        3 => "ProcessLookupError", // ESRCH
        4 => "InterruptedError", // EINTR
        10 => "ChildProcessError", // ECHILD
        11 => "BlockingIOError", // EAGAIN / EWOULDBLOCK
        32 => "BrokenPipeError", // EPIPE
        _ when windows => WindowsTypeName(errno),
        114 or 115 => "BlockingIOError", // EALREADY, EINPROGRESS
        108 => "BrokenPipeError", // ESHUTDOWN
        103 => "ConnectionAbortedError", // ECONNABORTED
        104 => "ConnectionResetError", // ECONNRESET
        110 => "TimeoutError", // ETIMEDOUT
        111 => "ConnectionRefusedError", // ECONNREFUSED
        _ => "OSError",
    };

    private static string WindowsTypeName(int errno) => errno switch
    {
        10035 or 10036 or 10037 => "BlockingIOError", // WSAEWOULDBLOCK, WSAEINPROGRESS, WSAEALREADY
        10058 => "BrokenPipeError", // WSAESHUTDOWN
        10053 => "ConnectionAbortedError", // WSAECONNABORTED
        10054 => "ConnectionResetError", // WSAECONNRESET
        138 or 10060 => "TimeoutError", // ETIMEDOUT, WSAETIMEDOUT
        10061 => "ConnectionRefusedError", // WSAECONNREFUSED
        _ => "OSError",
    };

    /// <summary>
    /// The <c>errno</c> behind a runtime I/O exception on <paramref name="path"/>, the one Python's <c>open()</c> raises for the same
    /// call. On Windows the exception's <c>HResult</c> carries the Win32 error, mapped as CPython's <c>winerror_to_errno</c> maps it
    /// (<see cref="WinErrorToErrno"/>). On Unix: the raw error the runtime keeps (on the exception itself or, for
    /// <see cref="UnauthorizedAccessException"/>, its inner exception); for a file or directory the runtime reports missing (which it
    /// does for <c>ENOTDIR</c> too: a component of <paramref name="path"/> that is a regular file), the kernel's answer to a
    /// <c>stat</c> of the path (<c>ENOTDIR</c>, <c>ENOENT</c>, <c>ELOOP</c>, ...), or <c>ENOENT</c> when no path is given or the probe
    /// cannot run; <c>ENAMETOOLONG</c> for a path that is too long; <c>EACCES</c> for any other refusal. Null when none is known.
    /// </summary>
    public static int? Errno(Exception exc, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(exc);
        static bool IsRaw(int value) => value is > 0 and < 4096;
        if (OperatingSystem.IsWindows() && (exc is IOException or UnauthorizedAccessException) && IsWin32HResult(exc.HResult))
        {
            return WinErrorToErrno(exc.HResult);
        }

        return exc switch
        {
            FileNotFoundException or DirectoryNotFoundException => MissingPathErrno(path),
            PathTooLongException => NameTooLong,
            UnauthorizedAccessException { InnerException: { } inner } when IsRaw(inner.HResult) => inner.HResult,
            UnauthorizedAccessException => PermissionDenied,
            IOException when IsRaw(exc.HResult) => exc.HResult,
            _ => null,
        };
    }

    private const int Win32Facility = unchecked((int)0x80070000);
    private const int FacilityMask = unchecked((int)0xFFFF0000);

    // An HRESULT of FACILITY_WIN32 (0x8007xxxx), the shape the runtime's I/O exceptions carry on Windows. The mask is an int: with a uint
    // mask the sign-extended value would be compared as a long and never equal the negative facility constant.
    private static bool IsWin32HResult(int value) => (value & FacilityMask) == Win32Facility;

    // open() on a path the runtime reports missing: the errno stat(2) answers for it (statx on Linux), else ENOENT.
    private static int MissingPathErrno(string? path)
        => path is not null && !OperatingSystem.IsWindows() && UnixFileType.StatError(path, followSymlinks: true) is { } error and > 0
            ? error
            : NoSuchFile;

    /// <summary>
    /// CPython's <c>winerror_to_errno</c> (<c>PC/errmap.h</c>): a Win32 error, or a <c>FACILITY_WIN32</c> <c>HRESULT</c> wrapping one,
    /// as the <c>errno</c> its <c>OSError</c> carries, in the MSVC numbering CPython is built with (<c>ENOTEMPTY</c> 41, <c>EILSEQ</c> 42,
    /// <c>ETIMEDOUT</c> 138); Winsock codes are errno values themselves (six of them the C values plus 10000).
    /// </summary>
    public static int WinErrorToErrno(int winerror)
    {
        if (IsWin32HResult(winerror))
        {
            winerror &= 0xFFFF;
        }

        if (winerror is >= 10000 and < 12000)
        {
            return winerror is 10004 or 10009 or 10013 or 10014 or 10022 or 10024 ? winerror - 10000 : winerror;
        }

        return winerror switch
        {
            2 or 3 or 15 or 18 or 53 or 67 or 161 or 206 => NoSuchFile,
            10 => 7, // E2BIG
            11 or (>= 188 and <= 202) => 8, // ENOEXEC
            6 or 114 or 130 => 9, // EBADF
            128 or 129 => 10, // ECHILD
            89 or 164 or 215 => 11, // EAGAIN
            7 or 8 or 9 or 1816 => 12, // ENOMEM
            5 or 16 or (>= 19 and <= 36) or 65 or 82 or 83 or 108 or 132 or 158 or 167 => PermissionDenied,
            80 or 183 => FileExists,
            17 => 18, // EXDEV
            267 => NotADirectory,
            4 => 24, // EMFILE
            112 => 28, // ENOSPC
            109 or 232 => 32, // EPIPE
            145 => 41, // ENOTEMPTY
            1113 => 42, // EILSEQ
            258 => 138, // ETIMEDOUT
            _ => InvalidArgument,
        };
    }
}
