using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DriftBuster.Backend.Infrastructure;

/// <summary>
/// <see cref="INtPathSystem"/> over the Windows calls CPython's <c>nt</c> module makes: <c>_getfinalpathname</c> is
/// <c>CreateFileW</c> (no access, every share mode, <c>FILE_FLAG_BACKUP_SEMANTICS</c>) then <c>GetFinalPathNameByHandleW</c> with
/// <c>VOLUME_NAME_DOS</c>; <c>_nt_readlink</c> reads the reparse data through the runtime (<see cref="FileSystemInfo.LinkTarget"/>,
/// which reads symbolic links and junctions); <c>_findfirstfile</c> lists the directory for the name.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed partial class WindowsNtPathSystem : INtPathSystem
{
    private const uint ShareAll = 0x1 | 0x2 | 0x4;
    private const uint OpenExisting = 3;
    private const uint BackupSemantics = 0x02000000;
    private const uint VolumeNameDos = 0;
    private const int ErrorFileNotFound = 2;
    private const int ErrorNotAReparsePoint = 4390;

    public static WindowsNtPathSystem Instance { get; } = new();

    public string CurrentDirectory => Directory.GetCurrentDirectory();

    public unsafe string GetFinalPathName(string path)
    {
        if (path.Contains('\0', StringComparison.Ordinal))
        {
            throw new PythonValueException("embedded null character", nameof(path));
        }

        using var handle = CreateFile(path, 0, ShareAll, nint.Zero, OpenExisting, BackupSemantics, nint.Zero);
        if (handle.IsInvalid)
        {
            throw new NtPathException(Marshal.GetLastPInvokeError(), "CreateFileW", path);
        }

        var size = GetFinalPathNameByHandle(handle, null, 0, VolumeNameDos);
        while (true)
        {
            if (size == 0)
            {
                throw new NtPathException(Marshal.GetLastPInvokeError(), "GetFinalPathNameByHandleW", path);
            }

            var buffer = new char[size];
            uint length;
            fixed (char* start = buffer)
            {
                length = GetFinalPathNameByHandle(handle, start, size, VolumeNameDos);
            }

            if (length == 0)
            {
                throw new NtPathException(Marshal.GetLastPInvokeError(), "GetFinalPathNameByHandleW", path);
            }

            if (length < size)
            {
                return new string(buffer, 0, checked((int)length));
            }

            size = length;
        }
    }

    public string ReadLink(string path)
    {
        var target = Info(path).LinkTarget;
        return target ?? throw new NtPathException(ErrorNotAReparsePoint, "readlink", path);
    }

    public bool IsLink(string path)
    {
        try
        {
            var info = Info(path);
            return info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint) && info.LinkTarget is not null;
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public string FindFirstFile(string path)
    {
        var (head, tail) = PythonNtPath.Split(path);
        try
        {
            var match = Directory.EnumerateFileSystemEntries(head.Length == 0 ? "." : head, tail).FirstOrDefault();
            return match is null ? throw new NtPathException(ErrorFileNotFound, "FindFirstFileW", path) : Path.GetFileName(match);
        }
        catch (Exception exc) when (exc is IOException and not NtPathException or UnauthorizedAccessException)
        {
            throw new NtPathException(exc.HResult & 0xFFFF, "FindFirstFileW", path);
        }
    }

    private static FileSystemInfo Info(string path)
        => Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
}
