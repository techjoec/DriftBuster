using System.IO.Compression;

using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Cli.Commands;

internal static partial class StagePortable
{
    private const string GuiExecutable = "DriftBuster.Gui.exe";
    private const string NextExecutable = "DriftBuster.Gui.next.exe";

    /// <summary>The .cmd and .ps1 launchers that set <c>DRIFTBUSTER_DEBUG=1</c>, and the readme.</summary>
    public static void WriteDebugLaunchers(string bundleDir)
    {
        WriteLaunchers(bundleDir, GuiExecutable);
        TextModeFile.WriteText(
            Path.Combine(bundleDir, "README.debug.txt"),
            "DriftBuster Win11 Portable Debug Bundle\n\n"
            + "Use Run-DriftBuster-Debug.cmd (or .ps1) to force DRIFTBUSTER_DEBUG=1.\n"
            + "Logs are written to %LOCALAPPDATA%\\DriftBuster\\logs\\debug.jsonl.\n");
    }

    private static void WriteLaunchers(string directory, string executable)
    {
        TextModeFile.WriteText(
            Path.Combine(directory, "Run-DriftBuster-Debug.cmd"),
            $"@echo off\nsetlocal\nset \"DRIFTBUSTER_DEBUG=1\"\nstart \"\" \"%~dp0{executable}\" %*\nendlocal\n");
        TextModeFile.WriteText(
            Path.Combine(directory, "Run-DriftBuster-Debug.ps1"),
            $"$env:DRIFTBUSTER_DEBUG = \"1\"\nStart-Process -FilePath (Join-Path $PSScriptRoot \"{executable}\") -ArgumentList $args\n");
    }

    /// <summary>Files copied with their modification times, then each directory's time.</summary>
    public static void CopyTree(string source, string destination)
    {
        if (TextModeFile.Exists(destination))
        {
            throw FileSystemError.Create(FileSystemError.FileExists, destination);
        }

        Directory.CreateDirectory(destination);
        foreach (var entry in Directory.EnumerateFileSystemEntries(source))
        {
            var target = Path.Combine(destination, Path.GetFileName(entry));
            if (Directory.Exists(entry))
            {
                CopyTree(entry, target);
            }
            else
            {
                PortableFileOps.CopyWithTimes(entry, target);
            }
        }

        Directory.SetLastWriteTimeUtc(destination, Directory.GetLastWriteTimeUtc(source));
    }

    /// <summary>
    /// <c>{bundle_dir}.zip</c> replaced by a deflated archive of the bundle under its own name, with an entry for every directory.
    /// </summary>
    public static string ZipBundle(string bundleDir)
    {
        var zipPath = $"{bundleDir}.zip";
        File.Delete(zipPath);
        using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        var name = Path.GetFileName(bundleDir);
        AddDirectoryEntry(archive, bundleDir, name);
        AddContents(archive, bundleDir, name);
        return zipPath;
    }

    // Top-down: each directory's subdirectory entries in sorted order, then its files, then the subdirectories' contents.
    private static void AddContents(ZipArchive archive, string directory, string name)
    {
        var children = Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.Ordinal).ToList();
        var subdirectories = children.Where(Directory.Exists).ToList();
        foreach (var child in subdirectories)
        {
            AddDirectoryEntry(archive, child, $"{name}/{Path.GetFileName(child)}");
        }

        foreach (var file in children.Where(child => !Directory.Exists(child)))
        {
            archive.CreateEntryFromFile(file, $"{name}/{Path.GetFileName(file)}", CompressionLevel.Optimal);
        }

        foreach (var child in subdirectories)
        {
            AddContents(archive, child, $"{name}/{Path.GetFileName(child)}");
        }
    }

    private static void AddDirectoryEntry(ZipArchive archive, string directory, string name)
    {
        var entry = archive.CreateEntry(name + "/", CompressionLevel.NoCompression);
        entry.LastWriteTime = new DateTimeOffset(Directory.GetLastWriteTime(directory));
        entry.ExternalAttributes = OperatingSystem.IsWindows() ? 0x10 : ((0x4000 | (int)File.GetUnixFileMode(directory)) << 16) | 0x10;
    }

    /// <summary>
    /// Replaces the stage directory with a copy of the bundle. When the old stage cannot be removed (a running GUI holds its executable),
    /// the bundle is copied over it instead, and an executable that cannot be replaced is written beside it as
    /// <c>DriftBuster.Gui.next.exe</c> with the launchers pointed at that copy.
    /// </summary>
    public static void StageBundle(string bundleDir, string stageDir, PortableFileOps? ops = null)
    {
        ops ??= PortableFileOps.Default;
        if (TextModeFile.Exists(stageDir))
        {
            try
            {
                ops.DeleteTree(stageDir);
            }
            catch (Exception exc) when (IsPermissionError(exc))
            {
                StageOverLockedExecutable(bundleDir, stageDir, ops);
                return;
            }
        }

        Directory.CreateDirectory(LexicalPath.Parent(stageDir));
        CopyTree(bundleDir, stageDir);
    }

    private static void StageOverLockedExecutable(string bundleDir, string stageDir, PortableFileOps ops)
    {
        Directory.CreateDirectory(stageDir);
        var copiedNextExecutable = false;
        foreach (var source in Directory.EnumerateFileSystemEntries(bundleDir, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(stageDir, Path.GetRelativePath(bundleDir, source));
            if (Directory.Exists(source))
            {
                Directory.CreateDirectory(destination);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            try
            {
                ops.CopyFile(source, destination);
            }
            catch (Exception exc) when (IsPermissionError(exc) && string.Equals(Path.GetFileName(destination), GuiExecutable, StringComparison.OrdinalIgnoreCase))
            {
                ops.CopyFile(source, Path.Combine(Path.GetDirectoryName(destination)!, NextExecutable));
                copiedNextExecutable = true;
            }
        }

        if (copiedNextExecutable)
        {
            WriteLaunchers(stageDir, NextExecutable);
        }
    }

    // Access refused: EACCES or EPERM (a Windows sharing violation maps to EACCES).
    private static bool IsPermissionError(Exception exc)
        => exc is UnauthorizedAccessException
            || (exc is IOException && FileSystemError.Errno(exc) is FileSystemError.PermissionDenied or FileSystemError.OperationNotPermitted);
}
