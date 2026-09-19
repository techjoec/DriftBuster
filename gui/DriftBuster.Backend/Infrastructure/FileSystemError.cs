using System.Runtime.InteropServices;

namespace DriftBuster.Backend.Infrastructure;

/// <summary>
/// File-system failures as the .NET exceptions the runtime raises for them, with the runtime's message wording. The native calls
/// (<see cref="UnixFileType"/>) report a POSIX <c>errno</c>; <see cref="Create"/> turns one into the exception
/// the runtime would have raised, and <see cref="Errno"/> classifies a runtime exception back into the constants below.
/// </summary>
public static class FileSystemError
{
    public const int OperationNotPermitted = 1;
    public const int NoSuchFile = 2;
    public const int PermissionDenied = 13;
    public const int FileExists = 17;
    public const int NotADirectory = 20;
    public const int IsADirectory = 21;
    public const int InvalidArgument = 22;
    public const int NameTooLong = 36;
    public const int TooManyLinks = 40;

    // An exception built by Create, or raised by the runtime on Unix, carries the errno itself as HResult.
    private const int MaxErrno = 4095;

    // FACILITY_WIN32 HRESULTs wrap a Win32 system error code in their low 16 bits.
    private const uint Win32FacilityMask = 0xFFFF0000;
    private const uint Win32Facility = 0x80070000;

    /// <summary>
    /// The exception for <paramref name="errno"/> naming <paramref name="path"/>, with <see cref="Exception.HResult"/> set to the errno:
    /// <see cref="FileNotFoundException"/>, <see cref="UnauthorizedAccessException"/>, <see cref="PathTooLongException"/>, or an
    /// <see cref="IOException"/> for any other failure.
    /// </summary>
    public static Exception Create(int errno, string path, Exception? inner = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        Exception exception = errno switch
        {
            NoSuchFile => new FileNotFoundException($"Could not find file '{path}'.", path, inner),
            OperationNotPermitted or PermissionDenied or IsADirectory => AccessDeniedException(path, inner),
            FileExists => new IOException($"The file '{path}' already exists.", inner),
            NotADirectory => new IOException($"The path '{path}' is not a directory.", inner),
            NameTooLong => new PathTooLongException($"The path '{path}' is too long.", inner),
            TooManyLinks => new IOException($"Too many levels of symbolic links in the path '{path}'.", inner),
            _ => new IOException($"{Description(errno)}: '{path}'.", inner),
        };
        exception.HResult = errno;
        return exception;
    }

    /// <summary>The <see cref="UnauthorizedAccessException"/> the runtime raises for a path it may not open, a directory included.</summary>
    public static UnauthorizedAccessException AccessDenied(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return AccessDeniedException(path, inner: null);
    }

    /// <summary>Rejects a path holding a NUL character with the runtime's <see cref="ArgumentException"/> text.</summary>
    /// <exception cref="ArgumentException">The path holds a NUL character.</exception>
    public static void ThrowIfEmbeddedNull(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("Null character in path.", nameof(path));
        }
    }

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

    private static UnauthorizedAccessException AccessDeniedException(string path, Exception? inner)
        => new($"Access to the path '{path}' is denied.", inner);

    // The C library's description of an errno this class does not name.
    private static string Description(int errno)
        => OperatingSystem.IsWindows() ? $"Unknown error {errno}" : Marshal.GetPInvokeErrorMessage(errno);

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
