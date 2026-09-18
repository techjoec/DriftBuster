using System.Numerics;

using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Cli.Commands;

internal static partial class PurgeReportingRetention
{
    // Ticks to microseconds, rounded half to even as datetime.fromtimestamp rounds the fraction.
    private static BigInteger MicrosecondsOf(long ticks)
    {
        var sinceEpoch = ticks - DateTime.UnixEpoch.Ticks;
        var micros = Math.DivRem(sinceEpoch, TimeSpan.TicksPerMicrosecond, out var remainder);
        if (remainder < 0)
        {
            micros--;
            remainder += TimeSpan.TicksPerMicrosecond;
        }

        return remainder > 5 || (remainder == 5 && (micros & 1) == 1) ? micros + 1 : micros;
    }

    // entry.stat().st_mtime; null when the entry is gone or a link whose target is.
    private static BigInteger? ModifiedMicroseconds(string entry)
    {
        FileSystemInfo info = Directory.Exists(entry) ? new DirectoryInfo(entry) : new FileInfo(entry);
        if (info.LinkTarget is not null)
        {
            var target = info.ResolveLinkTarget(returnFinalTarget: true);
            if (target is null || !target.Exists)
            {
                return null;
            }

            info = target;
        }
        else if (!info.Exists)
        {
            return null;
        }

        return MicrosecondsOf(info.LastWriteTimeUtc.Ticks);
    }

    // Path ordering: component by component by code point (case-folded on Windows).
    private static int ComparePaths(string left, string right)
        => OperatingSystem.IsWindows()
            ? PathText.ComparePosixPaths(EngineText.Lower(left).Replace('\\', '/'), EngineText.Lower(right).Replace('\\', '/'))
            : PathText.ComparePosixPaths(left, right);

    // os.walk(path, topdown=False): below each directory, its subdirectories first, then its files unlinked and its subdirectories removed.
    private static void DeleteContents(string directory)
    {
        var entries = Directory.EnumerateFileSystemEntries(directory).ToList();
        var subdirectories = entries.Where(Directory.Exists).ToList();
        foreach (var subdirectory in subdirectories.Where(entry => new DirectoryInfo(entry).LinkTarget is null))
        {
            DeleteContents(subdirectory);
        }

        foreach (var file in entries.Except(subdirectories, StringComparer.Ordinal))
        {
            File.Delete(file);
        }

        foreach (var subdirectory in subdirectories)
        {
            RemoveDirectory(subdirectory);
        }
    }

    // Path.rmdir(): a link to a directory is not a directory to remove.
    private static void RemoveDirectory(string path)
    {
        if (new DirectoryInfo(path).LinkTarget is not null)
        {
            throw FileSystemError.Create(FileSystemError.NotADirectory, path);
        }

        Directory.Delete(path);
    }
}
