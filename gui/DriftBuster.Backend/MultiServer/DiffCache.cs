using System.Text;

using DriftBuster.Backend.Diff;
using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.MultiServer;

/// <summary>
/// One JSON file per host and config, <c>&lt;root&gt;/&lt;sha1("{host}:{config}")&gt;.json</c>: canonical payload, content type,
/// detection metadata, file hash and signature.
/// </summary>
/// <remarks>
/// Writes go to a temporary file beside the target (following a symlink entry) and are renamed over it, so a failed or cancelled
/// write never leaves a partial entry. Two exceptions keep in-place semantics: an existing entry that cannot be opened for writing
/// fails, and when the directory refuses a temporary file the entry is written in place (the only write a crash can leave
/// partial). A missing entry loads as null; non-UTF-8 or non-object JSON throws <see cref="InvalidDataException"/>; I/O errors
/// propagate and fail the host as offline in <see cref="MultiServerRunner"/>.
/// </remarks>
public sealed class DiffCache
{
    private const string TemporarySuffix = ".tmp";

    /// <summary>Creates <paramref name="root"/> and its parents when missing.</summary>
    public DiffCache(string root)
    {
        ArgumentNullException.ThrowIfNull(root);
        Root = root;
        EnginePath.MakeDirectories(root);
    }

    public string Root { get; }

    /// <summary>Test seam: invoked with the temporary file's path after it is written and before it replaces the entry.</summary>
    internal Action<string>? TemporaryWritten { get; set; }

    public string EntryPath(string hostId, string configId)
        => Path.Combine(Root, MultiServerPlan.Sha1Hex($"{hostId}:{configId}") + ".json");

    /// <summary>
    /// Null when the entry does not exist (after following links), is not valid JSON or does not carry <paramref name="signature"/>;
    /// otherwise the stored payload.
    /// </summary>
    public OrderedDictionary<string, object?>? Load(string hostId, string configId, string signature)
    {
        var path = EntryPath(hostId, configId);
        var kind = UnixFileType.Stat(path, followSymlinks: true);
        if (kind is null ? !File.Exists(EnginePath.KernelPath(path)) && !Directory.Exists(EnginePath.KernelPath(path)) : kind == UnixFileType.Kind.Missing)
        {
            return null;
        }

        if (kind == UnixFileType.Kind.Directory || (kind is null && Directory.Exists(EnginePath.KernelPath(path))))
        {
            throw FileSystemError.AccessDenied(path);
        }

        var raw = EngineTextFile.ReadBytes(path, path);

        if (!EngineJson.TryLoads(EngineUtf8.Decode(raw), out var parsed))
        {
            return null;
        }

        if (parsed is not OrderedDictionary<string, object?> payload)
        {
            throw new InvalidDataException($"expected a JSON object, not '{EngineBuiltins.TypeName(parsed)}'");
        }

        return payload.TryGetValue("signature", out var stored) && stored is string storedText && string.Equals(storedText, signature, StringComparison.Ordinal)
            ? payload
            : null;
    }

    /// <summary>Stores <paramref name="payload"/> plus <c>signature</c> as UTF-8 JSON (sorted keys, non-ASCII kept), atomically.</summary>
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
                EngineTextFile.WriteBytes(target, bytes, path);
                return;
            }

            TemporaryWritten?.Invoke(temporary);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, target, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    // An existing entry that refuses opening for write (read-only) fails the save even though a rename could replace it. Only a
    // regular file is opened, so a FIFO never blocks here.
    private static void RequireWritableEntry(string target)
    {
        var kind = UnixFileType.Stat(target, followSymlinks: true);
        if (kind == UnixFileType.Kind.Regular || (kind is null && File.Exists(target)))
        {
            using var probe = new FileStream(target, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        }
    }

    // False when the directory refuses the temporary file (its partial bytes are removed by the caller); the entry is then written
    // in place, and fails or succeeds as that write does.
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

    // The file to write: the entry, or where a symlink entry leads (a dangling link's target is created). A directory or looping
    // link fails. A link to a non-UTF-8 name cannot be named, so the entry is written in place through the link.
    private static (string Target, bool Nameable) WriteTarget(string path)
    {
        var target = EnginePath.KernelPath(path);
        if (UnixFileType.Stat(path, followSymlinks: false) == UnixFileType.Kind.Other)
        {
            var physical = EnginePath.ResolvePhysicalPath(Path.GetFullPath(target), out var nameable)
                ?? throw FileSystemError.Create(FileSystemError.TooManyLinks, path);
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
            throw FileSystemError.AccessDenied(path);
        }
    }

    /// <summary>
    /// An explicit directory is created and resolved; otherwise <c>cache/diffs</c> under the resolved data root
    /// (<see cref="DriftbusterPaths.GetDataRoot"/>), after copying files from <c>&lt;repositoryRoot&gt;/artifacts/cache/diffs</c> when that
    /// exists and the destination is empty.
    /// </summary>
    public static string ResolveCacheDirectory(string? cacheDir, string? repositoryRoot)
    {
        if (!string.IsNullOrEmpty(cacheDir))
        {
            return CreateAndResolve(EngineOsPath.ExpandUser(cacheDir));
        }

        // The data root resolved through symlinks, with cache/diffs appended as written.
        var destination = Path.Combine(CreateAndResolve(DriftbusterPaths.GetDataRoot()), "cache", "diffs");
        EnginePath.MakeDirectories(destination);
        if (!string.IsNullOrEmpty(repositoryRoot) && !DestinationHasEntries(destination))
        {
            MigrateLegacyDiffCache(repositoryRoot, destination);
        }

        return destination;
    }

    // Creates and resolves the path as the kernel walks it (a ".." after a symlink steps to the target's parent). A directory whose
    // physical path is not UTF-8 keeps the kernel's spelling, which reaches it through its links.
    private static string CreateAndResolve(string path)
    {
        EnginePath.MakeDirectories(path);
        return EnginePath.Resolve(path);
    }

    // Any entry present; an error counts as present, which ends the best-effort migration.
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
    /// Best effort: copies every file of <c>&lt;repositoryRoot&gt;/artifacts/cache/diffs</c> the cache does not hold; failures are ignored.
    /// The GUI facade runs this before every scan, even into a non-empty cache.
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
            var destination = EnginePath.KernelPath(cacheDirectory);
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
