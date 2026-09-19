using System.Globalization;

using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Json;
using DriftBuster.Backend.Models;
using DriftBuster.Backend.Secrets;

namespace DriftBuster.Backend.Profiles.Run;

/// <summary>
/// Runs a profile: validates (and optionally saves) it, copies every source's files through the secret filter into
/// <c>&lt;profile&gt;/raw/&lt;timestamp&gt;/&lt;alias or source_NN&gt;</c> in declared order, and writes the result as
/// <c>metadata.json</c> beside them.
/// </summary>
/// <remarks>
/// A source is a path or a glob (<see cref="PathWildcard"/> syntax, <c>**</c> for any depth). An optional source that is missing or
/// matches nothing is skipped; any other raises <see cref="RunProfileException"/>. Matches are taken in ordinal order; symbolic links,
/// matches inside an already collected directory and anything under the Profiles root are skipped; a directory's files keep their
/// relative paths, a file keeps its name; files matching the source's exclude patterns are left out.
/// </remarks>
public sealed class RunProfileExecutor(string? baseDir = null, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public RunProfileRunResult Execute(RunProfileDefinition profile, bool saveProfile, string? timestamp = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var profileDirectory = saveProfile ? RunProfileStore.Save(profile, baseDir) : ValidatedDirectory(profile);
        var runTimestamp = string.IsNullOrEmpty(timestamp)
            ? _time.GetUtcNow().ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture)
            : timestamp;
        var runRoot = Directory.CreateDirectory(Path.Join(profileDirectory, "raw", runTimestamp)).FullName;
        var profilesRoot = Path.GetFullPath(RunProfileStore.ProfilesRoot(baseDir));
        var context = SecretScanner.BuildContext(profile.SecretScanner);
        var log = new List<string>();
        var files = new List<RunProfileFileResult>();
        var sources = new List<RunProfileSourceResult>();
        for (var index = 0; index < profile.Sources.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = profile.Sources[index];
            var name = string.IsNullOrEmpty(source.Alias)
                ? string.Create(CultureInfo.InvariantCulture, $"source_{index:00}")
                : RunProfileStore.SafeName(source.Alias);
            var collector = new SourceCollector(source, index, Path.Join(runRoot, name), profilesRoot, context, log.Add, files);
            sources.Add(collector.Collect(name, cancellationToken));
        }

        var result = new RunProfileRunResult(
            profile,
            runTimestamp,
            runRoot,
            string.IsNullOrEmpty(profile.Baseline) ? profile.Sources[0].Path : profile.Baseline,
            sources,
            files,
            SecretScanner.Summarise(context, log));
        AtomicFile.WriteAllText(Path.Join(runRoot, "metadata.json"), ModelJson.Serialize(result));
        return result;
    }

    /// <summary>True when a pattern matches the relative path (forward slashes) or its last segment.</summary>
    internal static bool ShouldExclude(string relativePath, IReadOnlyList<string> patterns)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        ArgumentNullException.ThrowIfNull(patterns);
        var name = relativePath[(relativePath.LastIndexOf('/') + 1)..];
        return patterns.Any(pattern => PathWildcard.IsMatch(relativePath, pattern) || PathWildcard.IsMatch(name, pattern));
    }

    private string ValidatedDirectory(RunProfileDefinition profile)
    {
        RunProfileStore.Validate(profile);
        return RunProfileStore.ProfileDirectory(profile.Name, baseDir);
    }

    private sealed class SourceCollector(
        RunProfileSource source,
        int index,
        string destinationRoot,
        string profilesRoot,
        SecretDetectionContext context,
        Action<string> log,
        List<RunProfileFileResult> files)
    {
        private readonly List<string> _matched = [];
        private readonly List<string> _collectedDirectories = [];

        public RunProfileSourceResult Collect(string directoryName, CancellationToken cancellationToken)
        {
            var matches = Matches(cancellationToken);
            if (matches.Count == 0)
            {
                var reason = PathWildcard.HasWildcards(source.Path) ? "no-matches" : "missing";
                return source.Optional
                    ? new RunProfileSourceResult(source.Path, directoryName, Optional: true, Skipped: true, reason, [])
                    : throw new RunProfileException($"sources[{index}].path: {(string.Equals(reason, "missing", StringComparison.Ordinal) ? "does not exist" : "matches nothing")}: {source.Path}");
            }

            Directory.CreateDirectory(destinationRoot);
            foreach (var match in matches)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsLink(match) || _collectedDirectories.Any(directory => IsInside(match, directory)))
                {
                    continue;
                }

                if (Directory.Exists(match))
                {
                    _collectedDirectories.Add(match);
                    foreach (var file in FileTreeGlob.Glob(match, "**/*", cancellationToken).Where(IsCollectable).Order(StringComparer.Ordinal))
                    {
                        Copy(file, match, cancellationToken);
                    }
                }
                else if (IsCollectable(match))
                {
                    Copy(match, Path.GetDirectoryName(match)!, cancellationToken);
                }
            }

            return new RunProfileSourceResult(source.Path, directoryName, source.Optional, Skipped: false, Reason: null, _matched);
        }

        // Full paths in ordinal order, each once, none under the Profiles root.
        private List<string> Matches(CancellationToken cancellationToken)
        {
            var expanded = RunProfileStore.Expand(source.Path);
            IEnumerable<string> found = PathWildcard.HasWildcards(expanded)
                ? FileTreeGlob.GlobPathname(expanded, cancellationToken)
                : RunProfileStore.Exists(expanded) ? [expanded] : [];
            return [.. found.Select(Path.GetFullPath).Where(path => !IsInside(path, profilesRoot)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        }

        private bool IsCollectable(string path) => EnginePath.IsFile(path) && !IsLink(path) && !IsInside(Path.GetFullPath(path), profilesRoot);

        private void Copy(string file, string basePath, CancellationToken cancellationToken)
        {
            var relative = Path.GetRelativePath(basePath, file).Replace('\\', '/');
            if (ShouldExclude(relative, source.Exclude))
            {
                return;
            }

            var destination = Path.Join(destinationRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            var (size, sha256) = SecretScanner.CopyWithSecretFilter(file, destination, relative, context, log, cancellationToken: cancellationToken);
            files.Add(new RunProfileFileResult(source.Path, destination.Replace('\\', '/'), size, sha256));
            _matched.Add(relative);
        }

        private static bool IsInside(string path, string directory)
        {
            var relative = Path.GetRelativePath(directory, path);
            return string.Equals(relative, ".", StringComparison.Ordinal)
                || (!string.Equals(relative, "..", StringComparison.Ordinal) && !relative.StartsWith("../", StringComparison.Ordinal) && !relative.StartsWith(@"..\", StringComparison.Ordinal) && !Path.IsPathRooted(relative));
        }

        private static bool IsLink(string path)
        {
            try
            {
                return new FileInfo(path).LinkTarget is not null;
            }
            catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }
}
