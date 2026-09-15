using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Models;

namespace DriftBuster.Backend.Profiles.Run;

public static partial class RunProfileExecutor
{
    private static readonly Comparer<string> CodePointOrder = Comparer<string>.Create(PathText.CompareCodePoints);

    /// <summary>
    /// <c>offline_runner._expand_path(text)</c> as <c>_iter_source_matches</c> expands it:
    /// <c>os.path.expanduser(os.path.expandvars(text))</c>, left relative to the working directory.
    /// </summary>
    internal static string ExpandStructuredPath(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return PythonOsPath.ExpandUser(PythonOsPath.ExpandVars(text));
    }

    /// <summary>
    /// <c>_iter_source_matches(path_text)</c> without a base directory: when the unexpanded text holds no glob character, the expanded
    /// path if it exists; otherwise the distinct <c>glob.glob(expanded, recursive=True)</c> results. Nothing found raises
    /// <c>FileNotFoundError("Path does not exist: {path_text}")</c>, and so does a path whose every match <paramref name="isOwnOutput"/>
    /// names: the offline runner writes its output elsewhere, so there the run's own output does not exist to be matched.
    /// </summary>
    internal static IReadOnlyList<string> CollectStructuredMatches(
        string pathText,
        Func<string, bool>? isOwnOutput = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pathText);
        var expanded = ExpandStructuredPath(pathText);
        IEnumerable<string> found;
        if (!RunProfileStore.HasMagic(pathText))
        {
            var candidate = PythonPurePath.Str(expanded);
            found = RunProfileStore.Exists(candidate) ? [candidate] : [];
        }
        else
        {
            found = PythonModuleGlob.Glob(expanded, recursive: true, cancellationToken)
                .Order(CodePointOrder)
                .Distinct(StringComparer.Ordinal)
                .Select(PythonPurePath.Str);
        }

        var matches = found.Where(match => isOwnOutput is null || !isOwnOutput(match)).ToList();
        return matches.Count > 0 ? matches : throw MissingSource(pathText);
    }

    internal static FileNotFoundException MissingSource(string pathText) => new($"Path does not exist: {pathText}");

    // One source of a structured profile, as execute_config collects an OfflineCollectionSource: an optional source that is missing or
    // matches nothing is skipped; the matches in posix order, each symlink skipped and each match inside a directory collected before
    // skipped; a directory's files (rglob("*"), is_file()) copied in posix order under their path relative to it, a file under its name,
    // and any of them matching an exclude pattern left out. Plan decision: nothing inside the run's own profile directory (its profile.json
    // and raw/ tree, which the offline runner writes elsewhere) is collected, whether a match names it or lies above it.
    private static ProfileRunSource CollectStructuredSource(
        RunProfileSource source,
        string destinationName,
        string destinationRoot,
        CopyTarget target,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<string> matches;
        try
        {
            matches = CollectStructuredMatches(source.Path, match => !IsSymlink(match) && target.IsOwnOutput(PhysicalPath(match)), cancellationToken);
        }
        catch (FileNotFoundException) when (source.Optional)
        {
            var reason = RunProfileStore.HasMagic(source.Path) ? "no-matches" : "missing";
            return new ProfileRunSource(source.Path, destinationName, Optional: true, Skipped: true, reason, [], source.Exclude ?? []);
        }

        var matched = new List<string>();
        var processedDirectories = new List<string>();
        foreach (var match in matches.OrderBy(PathText.ToPosix, CodePointOrder))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsSymlink(match))
            {
                continue;
            }

            var resolved = PhysicalPath(match);
            if (processedDirectories.Any(directory => IsInside(resolved, directory)))
            {
                continue;
            }

            if (RunProfileStore.IsDirectory(match))
            {
                processedDirectories.Add(resolved);
                var files = WalkFiles(match, cancellationToken)
                    .Where(file => PythonPath.IsFile(file) && !target.IsOwnOutput(PhysicalPath(file)))
                    .OrderBy(PathText.ToPosix, CodePointOrder)
                    .ToList();
                foreach (var file in files)
                {
                    CopyUnlessExcluded(source, file, match, destinationRoot, target, matched, cancellationToken);
                }
            }
            else if (PythonPath.IsFile(match))
            {
                CopyUnlessExcluded(source, match, PythonPurePath.Parent(match), destinationRoot, target, matched, cancellationToken);
            }
        }

        return new ProfileRunSource(source.Path, destinationName, source.Optional, Skipped: false, Reason: null, matched, source.Exclude ?? []);
    }

    // The path is the directory or lies below it (both physical paths).
    private static bool IsInside(string path, string directory) => PythonPurePath.RelativeTo(path, directory) is not null;

    // Path.is_symlink(): an lstat that reports a link; a path that cannot be looked up is not one.
    private static bool IsSymlink(string path)
    {
        try
        {
            return new FileInfo(PythonPath.KernelPath(path)).LinkTarget is not null;
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }
}
