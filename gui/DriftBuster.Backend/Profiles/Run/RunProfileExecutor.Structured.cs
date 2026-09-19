using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Models;

namespace DriftBuster.Backend.Profiles.Run;

public static partial class RunProfileExecutor
{
    private static readonly Comparer<string> CodePointOrder = Comparer<string>.Create(PathText.CompareCodePoints);

    /// <summary>Expands environment variables and <c>~</c>; relative paths stay relative to the working directory.</summary>
    internal static string ExpandStructuredPath(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return EngineOsPath.ExpandUser(EngineOsPath.ExpandVars(text));
    }

    /// <summary>
    /// For wildcard-free text, the expanded path when it exists; otherwise the distinct glob matches. Nothing found (or only matches
    /// <paramref name="isOwnOutput"/> names) throws <see cref="FileNotFoundException"/> (<c>Path does not exist: {path_text}</c>), since
    /// the offline runner writes its output elsewhere.
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
            var candidate = LexicalPath.Str(expanded);
            found = RunProfileStore.Exists(candidate) ? [candidate] : [];
        }
        else
        {
            found = FileTreeGlob.GlobPathname(expanded, cancellationToken)
                .Order(CodePointOrder)
                .Distinct(StringComparer.Ordinal)
                .Select(LexicalPath.Str);
        }

        var matches = found.Where(match => isOwnOutput is null || !isOwnOutput(match)).ToList();
        return matches.Count > 0 ? matches : throw MissingSource(pathText);
    }

    internal static FileNotFoundException MissingSource(string pathText) => new($"Path does not exist: {pathText}");

    // One structured source, collected like the offline runner: a missing or empty optional source is skipped; matches in posix
    // order, symlinks and matches inside an already collected directory skipped; a directory's files copied in posix order under
    // their relative path, a file under its name; excludes left out. Nothing inside the run's own profile directory is collected.
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
                    .Where(file => EnginePath.IsFile(file) && !target.IsOwnOutput(PhysicalPath(file)))
                    .OrderBy(PathText.ToPosix, CodePointOrder)
                    .ToList();
                foreach (var file in files)
                {
                    CopyUnlessExcluded(source, file, match, destinationRoot, target, matched, cancellationToken);
                }
            }
            else if (EnginePath.IsFile(match))
            {
                CopyUnlessExcluded(source, match, LexicalPath.Parent(match), destinationRoot, target, matched, cancellationToken);
            }
        }

        return new ProfileRunSource(source.Path, destinationName, source.Optional, Skipped: false, Reason: null, matched, source.Exclude ?? []);
    }

    // The path is the directory or lies below it (both physical paths).
    private static bool IsInside(string path, string directory) => LexicalPath.RelativeTo(path, directory) is not null;

    // A path that cannot be looked up is not a link.
    private static bool IsSymlink(string path)
    {
        try
        {
            return new FileInfo(EnginePath.KernelPath(path)).LinkTarget is not null;
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }
}
