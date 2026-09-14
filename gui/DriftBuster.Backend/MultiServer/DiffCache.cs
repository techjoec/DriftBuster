using System.Text;

using DriftBuster.Backend.Diff;
using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.MultiServer;

/// <summary>
/// <c>driftbuster.multi_server.DiffCache</c>: one JSON file per host and config, <c>&lt;root&gt;/&lt;sha1("{host}:{config}")&gt;.json</c>,
/// holding the canonical payload, content type, detection metadata, file hash and signature.
/// </summary>
/// <remarks>
/// Writes are atomic where Python's in-place <c>write_text</c> would succeed: the entry is written to a temporary file beside the
/// file the entry path leads to (through a symlink, as <c>write_text</c> follows it) and renamed over that file, so a cancelled or
/// failed write never leaves a partial entry. The outcome stays Python's in the two cases a rename changes it: an existing entry
/// that cannot be opened for writing fails as <c>open(path, "w")</c> fails, even though a rename could replace it, and when the
/// directory refuses the temporary file the entry is written in place, as Python writes it (an existing writable entry in a
/// directory that refuses new names is truncated and rewritten; a missing one fails with Python's error). That in-place write is
/// the only one a crash can leave partial; cancellation is checked before it starts. Every other outcome is Python's too: a
/// missing entry (a dangling link included) loads as null; a file that is not UTF-8, or whose JSON is not an object, raises with
/// the <c>UnicodeDecodeError</c> or <c>AttributeError</c> text; an I/O failure raises with the <c>OSError</c> text naming the
/// entry path (<see cref="PythonOSError"/>). A raise fails the host as offline in <see cref="MultiServerRunner"/>.
/// </remarks>
public sealed class DiffCache
{
    private const string TemporarySuffix = ".tmp";

    /// <summary>Creates <paramref name="root"/> (and its parents) when missing, as <c>mkdir(parents=True, exist_ok=True)</c>.</summary>
    public DiffCache(string root)
    {
        ArgumentNullException.ThrowIfNull(root);
        Root = root;
        Directory.CreateDirectory(PythonPath.KernelPath(root));
    }

    public string Root { get; }

    /// <summary>Test seam: invoked with the temporary file's path after it is written and before it replaces the entry.</summary>
    internal Action<string>? TemporaryWritten { get; set; }

    /// <summary>The entry path for <paramref name="hostId"/> and <paramref name="configId"/>.</summary>
    public string EntryPath(string hostId, string configId)
        => Path.Combine(Root, MultiServerPlan.Sha1Hex($"{hostId}:{configId}") + ".json");

    /// <summary>
    /// <c>DiffCache.load</c>: null when the entry does not exist (after following links), is not valid JSON or does not carry
    /// <paramref name="signature"/>; otherwise the stored payload.
    /// </summary>
    public OrderedDictionary<string, object?>? Load(string hostId, string configId, string signature)
    {
        var path = EntryPath(hostId, configId);
        var kind = UnixFileType.Stat(path, followSymlinks: true);
        if (kind is null ? !File.Exists(PythonPath.KernelPath(path)) && !Directory.Exists(PythonPath.KernelPath(path)) : kind == UnixFileType.Kind.Missing)
        {
            return null;
        }

        if (kind == UnixFileType.Kind.Directory || (kind is null && Directory.Exists(PythonPath.KernelPath(path))))
        {
            throw PythonOSError.Create(PythonOSError.IsADirectory, path);
        }

        byte[] raw;
        try
        {
            raw = File.ReadAllBytes(PythonPath.KernelPath(path));
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
        {
            throw PythonOSError.Errno(exc) is { } errno ? PythonOSError.Create(errno, path, exc) : exc;
        }

        if (!PythonJson.TryLoads(PythonUtf8.Decode(raw), out var parsed))
        {
            return null;
        }

        if (parsed is not OrderedDictionary<string, object?> payload)
        {
            throw new InvalidDataException($"'{PythonBuiltins.TypeName(parsed)}' object has no attribute 'get'");
        }

        return payload.TryGetValue("signature", out var stored) && stored is string storedText && string.Equals(storedText, signature, StringComparison.Ordinal)
            ? payload
            : null;
    }

    /// <summary>
    /// Stores <paramref name="payload"/> plus <c>signature</c> as <c>json.dumps(data, ensure_ascii=False, sort_keys=True)</c>
    /// in UTF-8, atomically, into the file the entry path leads to.
    /// </summary>
    public void Save(string hostId, string configId, string signature, OrderedDictionary<string, object?> payload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var data = new OrderedDictionary<string, object?>(payload, StringComparer.Ordinal)
        {
            ["signature"] = signature,
        };
        var bytes = Encoding.UTF8.GetBytes(Canonicaliser.DumpsSorted(data, indent: false));
        var path = EntryPath(hostId, configId);
        cancellationToken.ThrowIfCancellationRequested();
        var (target, nameable) = WriteTarget(path);
        var temporary = Path.Combine(Path.GetDirectoryName(target) ?? Root, $".{Path.GetFileName(target)}.{Guid.NewGuid():N}{TemporarySuffix}");
        try
        {
            RequireWritableEntry(target);
            if (!nameable || !TryWriteTemporary(temporary, bytes))
            {
                cancellationToken.ThrowIfCancellationRequested();
                File.WriteAllBytes(target, bytes);
                return;
            }

            TemporaryWritten?.Invoke(temporary);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, target, overwrite: true);
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
        {
            throw PythonOSError.Errno(exc) is { } errno ? PythonOSError.Create(errno, path, exc) : exc;
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    // open(path, "w") opens an existing file for writing before it truncates it: an entry that refuses that (a read-only file)
    // fails the save even though a rename could replace it. Opening without truncating changes nothing; only a regular file is
    // opened, so a FIFO entry never blocks here.
    private static void RequireWritableEntry(string target)
    {
        var kind = UnixFileType.Stat(target, followSymlinks: true);
        if (kind == UnixFileType.Kind.Regular || (kind is null && File.Exists(target)))
        {
            using var probe = new FileStream(target, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        }
    }

    // False when the directory refuses the temporary file (its partial bytes are removed by the caller); the entry is then written
    // in place, as Python writes it, and fails or succeeds as that write does.
    private static bool TryWriteTemporary(string temporary, byte[] bytes)
    {
        try
        {
            File.WriteAllBytes(temporary, bytes);
            return true;
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // The file open(path, "w") writes: the entry itself, or where a symlink entry leads (a dangling link's target is created).
    // A directory, or a link that loops, fails as open() fails. A link leading to a name that is not UTF-8 cannot be named: the
    // entry is then written in place through the link (not nameable), as open() writes it, instead of beside a guessed name.
    private static (string Target, bool Nameable) WriteTarget(string path)
    {
        var target = PythonPath.KernelPath(path);
        if (UnixFileType.Stat(path, followSymlinks: false) == UnixFileType.Kind.Other)
        {
            var physical = PythonPath.ResolvePhysicalPath(Path.GetFullPath(target), out var nameable)
                ?? throw PythonOSError.Create(PythonOSError.TooManyLinks, path);
            if (!nameable)
            {
                RequireNotDirectory(path, target);
                return (target, false);
            }

            target = physical;
        }

        RequireNotDirectory(path, target);
        return (target, true);
    }

    private static void RequireNotDirectory(string path, string target)
    {
        if (UnixFileType.Stat(target, followSymlinks: true) == UnixFileType.Kind.Directory || (!OperatingSystem.IsLinux() && Directory.Exists(target)))
        {
            throw PythonOSError.Create(PythonOSError.IsADirectory, path);
        }
    }

    /// <summary>
    /// <c>_resolve_cache_dir(cache_dir)</c>: an explicit directory is created and resolved; otherwise the <c>cache/diffs</c>
    /// directory under the data root (<see cref="DriftbusterPaths.GetDataRoot"/>, resolved through symlinks) is used, after <c>_migrate_legacy_cache</c>:
    /// when <c>&lt;repositoryRoot&gt;/artifacts/cache/diffs</c> exists and the destination is empty, every legacy file the
    /// destination does not hold is copied. Python reads the legacy entries relative to the working directory; the port takes the
    /// directory that working directory would be.
    /// </summary>
    public static string ResolveCacheDirectory(string? cacheDir, string? repositoryRoot)
    {
        if (!string.IsNullOrEmpty(cacheDir))
        {
            return CreateAndResolve(PythonOsPath.ExpandUser(cacheDir));
        }

        // _resolve_data_root() returns the data root resolved through symlinks; cache/diffs is appended as written.
        var destination = Path.Combine(CreateAndResolve(DriftbusterPaths.GetDataRoot()), "cache", "diffs");
        Directory.CreateDirectory(PythonPath.KernelPath(destination));
        if (!string.IsNullOrEmpty(repositoryRoot) && !DestinationHasEntries(destination))
        {
            MigrateLegacyDiffCache(repositoryRoot, destination);
        }

        return destination;
    }

    // path.mkdir(parents=True, exist_ok=True) then path.resolve(): both as the kernel walks the path, so a ".." after a symlink
    // steps to the parent of the link's target (the runtime would remove it lexically first).
    // A directory whose physical path holds a name that is not UTF-8 keeps the kernel's spelling of it instead, which reaches the
    // same directory through its links (Python's resolve() gives the surrogateescape name, which its file calls accept).
    private static string CreateAndResolve(string path)
    {
        var kernel = PythonPath.KernelPath(path);
        Directory.CreateDirectory(kernel);
        var absolute = PythonPath.Absolute(path);
        var physical = PythonPath.ResolvePhysicalPath(absolute, out var nameable);
        return physical is null ? Path.GetFullPath(absolute) : nameable ? physical : Path.GetFullPath(PythonPath.KernelPath(absolute));
    }

    // any(destination.iterdir()); an error counts as entries present, which ends the best-effort migration as Python's does.
    private static bool DestinationHasEntries(string destination)
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(destination).Any();
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>
    /// Best effort: copies every file of <c>&lt;repositoryRoot&gt;/artifacts/cache/diffs</c> that the cache directory does not
    /// already hold. Failures are ignored. This is also the GUI facade's migration before every scan, which (unlike
    /// <see cref="ResolveCacheDirectory"/>) copies into a cache that already holds entries.
    /// </summary>
    public static void MigrateLegacyDiffCache(string repositoryRoot, string cacheDirectory)
    {
        if (string.IsNullOrWhiteSpace(repositoryRoot))
        {
            return;
        }

        try
        {
            var legacyRoot = Path.Combine(repositoryRoot, "artifacts", "cache", "diffs");
            if (!Directory.Exists(legacyRoot))
            {
                return;
            }

            // The directory the kernel reaches, which is the one the resolved cache directory names.
            var destination = PythonPath.KernelPath(cacheDirectory);
            Directory.CreateDirectory(destination);
            foreach (var file in Directory.EnumerateFiles(legacyRoot, "*", SearchOption.TopDirectoryOnly))
            {
                var target = Path.Combine(destination, Path.GetFileName(file));
                if (!File.Exists(target))
                {
                    File.Copy(file, target, overwrite: false);
                }
            }
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // Migration is a best-effort convenience for developers; ignore failures.
        }
    }
}
