using System.Globalization;

using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Models;
using DriftBuster.Backend.Secrets;

namespace DriftBuster.Backend.Profiles.Run;

/// <summary>
/// <c>run_profiles.execute_profile</c>: saves the profile, copies every source's files through the secret filter into
/// <c>&lt;profile&gt;/raw/&lt;timestamp&gt;/&lt;source dir&gt;</c>, and writes <c>metadata.json</c> beside them.
/// </summary>
/// <remarks>
/// A profile whose sources all hold only a path runs as a plain profile. A profile with a structured source
/// (<see cref="RunProfile.IsStructured"/>) collects every source as <c>offline_runner.execute_config</c> does: in declared
/// order (the baseline is not moved to the front), with the options as the payload holds them (<see cref="RunProfile.SecretOptions"/>); an
/// <see cref="RunProfileSource.Alias"/> names the source's directory (<c>_safe_name(alias)</c>) in place of <c>source_NN</c>; an
/// <see cref="RunProfileSource.Optional"/> source that is missing or matches nothing is skipped; matches are expanded, globbed, sorted
/// and de-duplicated as <c>_iter_source_matches</c> does, a match that is a symlink is skipped, a match inside a directory already
/// collected is skipped; and a file whose relative path or name matches one of the <see cref="RunProfileSource.Exclude"/> patterns
/// (<c>fnmatch.fnmatch</c>) is not copied.
/// </remarks>
public static partial class RunProfileExecutor
{
    /// <summary>The clock <c>_timestamp()</c> reads; tests swap it.</summary>
    internal static Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

    /// <summary><c>_timestamp()</c>: <c>datetime.now(UTC).strftime("%Y%m%dT%H%M%SZ")</c>.</summary>
    public static string Timestamp() => UtcNow().ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

    /// <summary><c>execute_profile(profile, base_dir=..., timestamp=...)</c>.</summary>
    public static ProfileRunResult ExecuteProfile(RunProfile profile, string? baseDir = null, string? timestamp = null, CancellationToken cancellationToken = default)
        => ExecuteProfile(profile, baseDir, timestamp, saveProfile: true, cancellationToken);

    /// <summary>
    /// <see cref="ExecuteProfile(RunProfile, string?, string?, CancellationToken)"/>; with <paramref name="saveProfile"/> false the
    /// profile is validated as <c>save_profile</c> validates it but <c>profile.json</c> is not written (the GUI facade's choice).
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

    // The sources with the first one whose path is the baseline moved to the front (source_strings.remove / insert(0, ...)). A structured
    // profile keeps the declared order, as execute_config iterates it.
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

    /// <summary>
    /// The directory a source's files are copied under: <c>_safe_name(alias)</c> for a source with an alias, otherwise
    /// <c>source_{index:02d}</c> as <c>execute_profile</c> names it.
    /// </summary>
    public static string DestinationName(RunProfileSource source, int fallbackIndex)
    {
        ArgumentNullException.ThrowIfNull(source);
        return string.IsNullOrEmpty(source.Alias)
            ? string.Create(CultureInfo.InvariantCulture, $"source_{fallbackIndex:00}")
            : RunProfileStore.SafeName(source.Alias);
    }

    // Where the files of a run go: the file list, the secret context and log every copy goes through, and the run's own output (physical
    // paths), which a structured source never collects: the profile directory the run writes into, and each directory above it that the
    // run created (the Profiles root, or the base directory, when it did not exist before the run).
    private sealed record CopyTarget(List<ProfileFile> Files, SecretDetectionContext Context, Action<string> Log, IReadOnlyList<string> OwnOutput)
    {
        public bool IsOwnOutput(string physicalPath) => OwnOutput.Any(directory => IsInside(physicalPath, directory));
    }

    // The Profiles root and each directory above it that does not exist yet, nearest first: the directories profiles_root creates.
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

    // Path.exists(), with a lookup that raises counted as existing (the run then fails creating the directory).
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

    // The absolute path with every symlink the kernel would follow resolved, or the absolute path when it cannot be resolved.
    private static string PhysicalPath(string path) => EnginePath.ResolvePhysicalPath(EnginePath.Absolute(path)) ?? EnginePath.Absolute(path);

    // One source of a profile without structured sources (execute_profile): its matches, each directory walked with rglob("*") and each
    // file copied.
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

    // Every entry below a directory (FileTreeGlob with "**/*"), less each path whose name the runtime decoded with U+FFFD that names no entry
    // (a Linux name that is not UTF-8), and less every repeat of a path holding U+FFFD, so an entry whose name really holds U+FFFD is read
    // once and never in place of an undecodable sibling (platform limit, decision R).
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
    /// The path itself when it exists; the <see cref="FileTreeGlob.GlobPathname"/> matches (sorted by code point over their posix form)
    /// when it holds a wildcard; otherwise <see cref="FileNotFoundException"/> (<c>Path does not exist: ...</c>).
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
    /// <c>_copy_file(source=..., file=..., base=..., destination_root=..., secret_context=..., secret_log=...)</c>: the file is copied to
    /// <c>destination_root / file.relative_to(base)</c> (its name alone when it is not under <paramref name="basePath"/>), through
    /// <c>copy_with_secret_filter</c> when a context and log are given, otherwise with <c>shutil.copy2</c>.
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

    // file.relative_to(base) if file.is_relative_to(base) else Path(file.name), in posix form.
    private static string RelativePath(string file, string basePath) => LexicalPath.RelativeTo(file, basePath) ?? PathText.Name(file);
}
