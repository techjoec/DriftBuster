using System.Globalization;

using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Models;
using DriftBuster.Backend.Secrets;

namespace DriftBuster.Backend.Profiles.Run;

/// <summary>
/// Runs a profile: saves it, copies every source's files through the secret filter into
/// <c>&lt;profile&gt;/raw/&lt;timestamp&gt;/&lt;source dir&gt;</c>, and writes <c>metadata.json</c> beside them.
/// </summary>
/// <remarks>
/// Path-only profiles put the baseline source first. A structured profile (<see cref="RunProfile.IsStructured"/>) collects like the
/// offline runner: declared order; an <see cref="RunProfileSource.Alias"/> names the directory instead of <c>source_NN</c>;
/// optional sources that are missing or empty are skipped; matches are globbed, sorted and de-duplicated, symlinks and matches
/// inside an already collected directory skipped; files matching <see cref="RunProfileSource.Exclude"/> are not copied.
/// </remarks>
public static partial class RunProfileExecutor
{
    /// <summary>Clock for run timestamps (test seam).</summary>
    internal static Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

    /// <summary>UTC run timestamp, <c>yyyyMMddTHHmmssZ</c>.</summary>
    public static string Timestamp() => UtcNow().ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

    public static ProfileRunResult ExecuteProfile(RunProfile profile, string? baseDir = null, string? timestamp = null, CancellationToken cancellationToken = default)
        => ExecuteProfile(profile, baseDir, timestamp, saveProfile: true, cancellationToken);

    /// <summary>
    /// As the public overload; with <paramref name="saveProfile"/> false the profile is validated but <c>profile.json</c> is not
    /// written (the GUI's choice).
    /// </summary>
    internal static ProfileRunResult ExecuteProfile(RunProfile profile, string? baseDir, string? timestamp, bool saveProfile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var createdAncestors = MissingProfilesAncestors(baseDir);
        string profileDirectory;
        if (saveProfile)
        {
            profileDirectory = RunProfileStore.SaveProfile(profile, baseDir);
        }
        else
        {
            RunProfileStore.ValidateProfile(profile);
            profileDirectory = RunProfileStore.ProfileDirectory(profile.Name, baseDir);
        }

        var runTimestamp = string.IsNullOrEmpty(timestamp) ? Timestamp() : timestamp;
        var runRoot = RunProfileStore.JoinName(RunProfileStore.JoinName(profileDirectory, "raw"), runTimestamp);
        EnginePath.MakeDirectories(runRoot);

        var secretContext = SecretScanner.BuildContext(profile.SecretOptions, profile.SecretScanner);
        var secretLogs = new List<string>();
        var files = new List<ProfileFile>();
        var summaries = new List<ProfileRunSource>();

        var sources = profile.IsStructured ? profile.Sources.ToList() : BaselineFirst(profile);
        var ownOutput = createdAncestors.Select(PhysicalPath).Prepend(PhysicalPath(profileDirectory)).ToList();
        for (var index = 0; index < sources.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destinationName = DestinationName(sources[index], index);
            var destinationRoot = RunProfileStore.JoinName(runRoot, destinationName);
            EnginePath.MakeDirectories(destinationRoot);
            var target = new CopyTarget(files, secretContext, secretLogs.Add, ownOutput);
            summaries.Add(profile.IsStructured
                ? CollectStructuredSource(sources[index], destinationName, destinationRoot, target, cancellationToken)
                : CollectSource(sources[index], destinationName, destinationRoot, target, cancellationToken));
        }

        var secretMetadata = SecretScanner.RunSecretsMetadata(secretContext, secretLogs);
        var metadata = new OrderedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["profile"] = profile.ToDict(),
            ["timestamp"] = runTimestamp,
            ["baseline"] = sources.Count == 0 ? null : string.IsNullOrEmpty(profile.Baseline) ? sources[0].Path : profile.Baseline,
            ["files"] = files.Select(entry => (object?)entry.ToDict()).ToList(),
            ["secrets"] = secretMetadata,
        };
        RunProfileStore.WriteJson(RunProfileStore.JoinName(runRoot, "metadata.json"), metadata);
        return new ProfileRunResult(profile, runTimestamp, runRoot, files, secretMetadata)
        {
            RedactionGuards = [.. secretContext.RedactionGuards],
            Sources = summaries,
        };
    }

    // Path-only profiles move the first source whose path is the baseline to the front; structured profiles keep declared order.
    private static List<RunProfileSource> BaselineFirst(RunProfile profile)
    {
        var sources = profile.Sources.ToList();
        var baselineIndex = string.IsNullOrEmpty(profile.Baseline)
            ? -1
            : sources.FindIndex(source => string.Equals(source.Path, profile.Baseline, StringComparison.Ordinal));
        if (baselineIndex >= 0)
        {
            var baseline = sources[baselineIndex];
            sources.RemoveAt(baselineIndex);
            sources.Insert(0, baseline);
        }

        return sources;
    }

    /// <summary>A source's directory: its alias made safe, else <c>source_NN</c>.</summary>
    public static string DestinationName(RunProfileSource source, int fallbackIndex)
    {
        ArgumentNullException.ThrowIfNull(source);
        return string.IsNullOrEmpty(source.Alias)
            ? string.Create(CultureInfo.InvariantCulture, $"source_{fallbackIndex:00}")
            : RunProfileStore.SafeName(source.Alias);
    }

    // Where a run's files go: the file list, secret context and log, and the run's own output directories (the profile directory and
    // any directories above it the run created), which a structured source must never collect.
    private sealed record CopyTarget(List<ProfileFile> Files, SecretDetectionContext Context, Action<string> Log, IReadOnlyList<string> OwnOutput)
    {
        public bool IsOwnOutput(string physicalPath) => OwnOutput.Any(directory => IsInside(physicalPath, directory));
    }

    // The Profiles root and each missing directory above it, nearest first.
    private static List<string> MissingProfilesAncestors(string? baseDir)
    {
        var missing = new List<string>();
        var path = LexicalPath.Join(string.IsNullOrEmpty(baseDir) ? Directory.GetCurrentDirectory() : baseDir, "Profiles");
        while (!ExistsOrUnknown(path))
        {
            missing.Add(path);
            var parent = LexicalPath.Parent(path);
            if (string.Equals(parent, path, StringComparison.Ordinal))
            {
                break;
            }

            path = parent;
        }

        return missing;
    }

    // Existence, with a lookup that throws counted as existing (creating the directory then fails).
    private static bool ExistsOrUnknown(string path)
    {
        try
        {
            return RunProfileStore.Exists(path);
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    // Absolute path with links resolved, or the absolute path when resolution fails.
    private static string PhysicalPath(string path) => EnginePath.ResolvePhysicalPath(EnginePath.Absolute(path)) ?? EnginePath.Absolute(path);

    // One path-only source: its matches, directories walked recursively, each file copied.
    private static ProfileRunSource CollectSource(RunProfileSource source, string destinationName, string destinationRoot, CopyTarget target, CancellationToken cancellationToken)
    {
        var matched = new List<string>();
        foreach (var match in CollectMatches(RunProfileStore.ExpandPath(source.Path), cancellationToken))
        {
            if (RunProfileStore.IsDirectory(match))
            {
                foreach (var file in WalkFiles(match, cancellationToken))
                {
                    if (EnginePath.IsFile(file))
                    {
                        CopyUnlessExcluded(source, file, match, destinationRoot, target, matched, cancellationToken);
                    }
                }
            }
            else if (EnginePath.IsFile(match))
            {
                CopyUnlessExcluded(source, match, LexicalPath.Parent(match), destinationRoot, target, matched, cancellationToken);
            }
        }

        return new ProfileRunSource(source.Path, destinationName, source.Optional, Skipped: false, Reason: null, matched, source.Exclude ?? []);
    }

    // Every entry below a directory, minus paths whose names only exist as a U+FFFD decoding of non-UTF-8 bytes (and repeats of a
    // U+FFFD path), so a real U+FFFD name is read once and never in place of an undecodable sibling.
    private static IEnumerable<string> WalkFiles(string directory, CancellationToken cancellationToken)
    {
        var replaced = new HashSet<string>(StringComparer.Ordinal);
        return FileTreeGlob.Glob(directory, "**/*", cancellationToken)
            .Where(path => !path.Contains('\uFFFD', StringComparison.Ordinal) || (replaced.Add(path) && !EnginePath.IsUndecodableName(path)));
    }

    private static void CopyUnlessExcluded(
        RunProfileSource source,
        string file,
        string basePath,
        string destinationRoot,
        CopyTarget target,
        List<string> matched,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var relative = RelativePath(file, basePath);
        if (ShouldExclude(relative, source.Exclude))
        {
            return;
        }

        target.Files.Add(CopyFile(source.Path, file, basePath, destinationRoot, target.Context, target.Log, cancellationToken));
        matched.Add(relative);
    }

    /// <summary>
    /// True when a pattern matches the relative path (posix form) or its last segment (<see cref="PathWildcard"/> syntax).
    /// </summary>
    internal static bool ShouldExclude(string relativePosix, IReadOnlyList<string>? patterns)
    {
        ArgumentNullException.ThrowIfNull(relativePosix);
        if (patterns is null || patterns.Count == 0)
        {
            return false;
        }

        var name = relativePosix[(relativePosix.LastIndexOf('/') + 1)..];
        return patterns.Any(pattern => PathWildcard.IsMatch(relativePosix, pattern) || PathWildcard.IsMatch(name, pattern));
    }

    /// <summary>
    /// The path itself when it exists; its glob matches (sorted by posix path) when it holds a wildcard; otherwise
    /// <see cref="FileNotFoundException"/> (<c>Path does not exist: ...</c>).
    /// </summary>
    internal static IReadOnlyList<string> CollectMatches(string pathText, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pathText);
        var candidate = LexicalPath.Str(pathText);
        if (RunProfileStore.Exists(candidate))
        {
            return [candidate];
        }

        if (RunProfileStore.HasMagic(pathText))
        {
            return FileTreeGlob.GlobPathname(pathText, cancellationToken).Select(LexicalPath.Str).ToList();
        }

        throw new FileNotFoundException($"Path does not exist: {pathText}");
    }

    /// <summary>
    /// Copies a file to <paramref name="destinationRoot"/> at its path relative to <paramref name="basePath"/> (its name when not
    /// under it), through the secret filter when a context and log are given, else as a plain copy keeping timestamps.
    /// </summary>
    internal static ProfileFile CopyFile(
        string source,
        string file,
        string basePath,
        string destinationRoot,
        SecretDetectionContext? secretContext = null,
        Action<string>? secretLog = null,
        CancellationToken cancellationToken = default)
    {
        var relative = RelativePath(file, basePath);
        var destination = LexicalPath.Join(destinationRoot, relative);
        EnginePath.MakeDirectories(LexicalPath.Parent(destination));
        var (size, digest) = secretContext is not null && secretLog is not null
            ? SecretScanner.CopyWithSecretFilter(file, destination, relative, secretContext, secretLog, cancellationToken: cancellationToken)
            : SecretScanner.CopyVerbatim(file, destination);
        return new ProfileFile(source, destination, size, digest);
    }

    // Relative posix path under the base, or the file name.
    private static string RelativePath(string file, string basePath) => LexicalPath.RelativeTo(file, basePath) ?? PathText.Name(file);
}
