using System.Runtime.InteropServices;

namespace DriftBuster.Backend.Infrastructure;

/// <summary>
/// Operating-system errors reported as <c>[Errno N] description</c> text. An error number is a POSIX <c>errno</c> value: the named
/// constants below mean the same thing on every platform, and <see cref="Errno"/> classifies runtime exceptions into them.
/// </summary>
public static class OsError
{
    public const int OperationNotPermitted = 1;
    public const int NoSuchFile = 2;
    public const int PermissionDenied = 13;
    public const int FileExists = 17;
    public const int NotADirectory = 20;
    public const int IsADirectory = 21;
    public const int InvalidArgument = 22;
    public const int FileTooLarge = 27;
    public const int NameTooLong = 36;
    public const int TooManyLinks = 40;

    // An exception built by Create, or raised by the runtime on Unix, carries the errno itself as HResult.
    private const int MaxErrno = 4095;

    // FACILITY_WIN32 HRESULTs wrap a Win32 system error code in their low 16 bits.
    private const uint Win32FacilityMask = 0xFFFF0000;
    private const uint Win32Facility = 0x80070000;

    /// <summary>The error opening a directory as a file reports: access denied on Windows, "Is a directory" elsewhere.</summary>
    public static int DirectoryOpenErrno => OperatingSystem.IsWindows() ? PermissionDenied : IsADirectory;

    /// <summary>An <see cref="IOException"/> reading <c>[Errno N] description</c>, with <see cref="Exception.HResult"/> set to <paramref name="errno"/>.</summary>
    public static IOException Create(int errno, Exception? inner = null)
        => Build($"[Errno {errno}] {StrError(errno)}", errno, inner);

    /// <summary><see cref="Create(int, Exception?)"/> naming <paramref name="filename"/>, quoted by <see cref="EngineRepr.StrRepr"/>.</summary>
    public static IOException Create(int errno, string filename, Exception? inner = null)
    {
        ArgumentNullException.ThrowIfNull(filename);
        return Build($"[Errno {errno}] {StrError(errno)}: {EngineRepr.StrRepr(filename)}", errno, inner);
    }

    /// <summary>
    /// Rejects a path holding a NUL character: <c>embedded null byte</c>, or <c>{function}: embedded null character in path</c> when
    /// the failing operation is named.
    /// </summary>
    /// <exception cref="EngineValueException">The path holds a NUL character.</exception>
    public static void ThrowIfEmbeddedNull(string path, string? function = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!path.Contains('\0', StringComparison.Ordinal))
        {
            return;
        }

        var message = function is null ? "embedded null byte" : $"{function}: embedded null character in path";
        throw new EngineValueException(message, nameof(path));
    }

    /// <summary>
    /// The description of <paramref name="errno"/>: fixed texts for the named constants, the C library's text for any other value on
    /// Unix, and <c>Unknown error N</c> for any other value on Windows.
    /// </summary>
    public static string StrError(int errno) => errno switch
    {
        OperationNotPermitted => "Operation not permitted",
        NoSuchFile => "No such file or directory",
        PermissionDenied => "Permission denied",
        FileExists => "File exists",
        NotADirectory => "Not a directory",
        IsADirectory => "Is a directory",
        InvalidArgument => "Invalid argument",
        FileTooLarge => "File too large",
        NameTooLong => "File name too long",
        TooManyLinks => "Too many levels of symbolic links",
        _ when OperatingSystem.IsWindows() => $"Unknown error {errno}",
        _ => Marshal.GetPInvokeErrorMessage(errno),
    };

    /// <summary>The error kind name reported for <paramref name="errno"/>, the same on every platform.</summary>
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
    /// The errno <paramref name="exc"/> stands for, or null when it is not one of the known kinds. <paramref name="path"/>, when
    /// given, lets a not-found error on Unix be told apart from a path running through a file.
    /// </summary>
    public static int? Errno(Exception exc, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(exc);
        if (exc.HResult is > 0 and <= MaxErrno)
        {
            return exc.HResult;
        }

        return OperatingSystem.IsWindows() ? WindowsErrno(exc) : UnixErrno(exc, path);
    }

    private static IOException Build(string message, int errno, Exception? inner)
        => new(message, inner) { HResult = errno };

    private static int? UnixErrno(Exception exc, string? path) => exc switch
    {
        UnauthorizedAccessException { InnerException.HResult: > 0 and <= MaxErrno } => exc.InnerException.HResult,
        UnauthorizedAccessException => PermissionDenied,
        PathTooLongException => NameTooLong,
        FileNotFoundException or DirectoryNotFoundException => path is not null && RunsThroughFile(path) ? NotADirectory : NoSuchFile,
        _ => null,
    };

    // Win32 system error codes, as Microsoft's "System Error Codes" reference numbers them.
    private static int? WindowsErrno(Exception exc) => exc switch
    {
        FileNotFoundException or DirectoryNotFoundException => NoSuchFile,
        UnauthorizedAccessException => PermissionDenied,
        PathTooLongException => NameTooLong,
        _ => Win32Code(exc.HResult) switch
        {
            2 or 3 => NoSuchFile,              // ERROR_FILE_NOT_FOUND, ERROR_PATH_NOT_FOUND
            5 or 32 => PermissionDenied,       // ERROR_ACCESS_DENIED, ERROR_SHARING_VIOLATION
            80 or 183 => FileExists,           // ERROR_FILE_EXISTS, ERROR_ALREADY_EXISTS
            123 => InvalidArgument,            // ERROR_INVALID_NAME
            206 => NameTooLong,                // ERROR_FILENAME_EXCED_RANGE
            267 => NotADirectory,              // ERROR_DIRECTORY
            _ => null,
        },
    };

    private static int? Win32Code(int hresult)
        => ((uint)hresult & Win32FacilityMask) == Win32Facility ? hresult & 0xFFFF : null;

    // True when an existing ancestor of the path is something other than a directory.
    private static bool RunsThroughFile(string path)
    {
        try
        {
            for (var parent = Path.GetDirectoryName(path); !string.IsNullOrEmpty(parent); parent = Path.GetDirectoryName(parent))
            {
                if (Directory.Exists(parent))
                {
                    return false;
                }

                if (File.Exists(parent))
                {
                    return true;
                }
            }
        }
        catch (Exception exc) when (exc is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }

        return false;
    }
}
