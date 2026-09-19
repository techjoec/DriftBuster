using System.Globalization;

using DriftBuster.Backend.Profiles.Run;

namespace DriftBuster.Cli.Commands;

/// <summary>
/// <c>driftbuster maint purge-reporting-retention PATH...</c>: lists the entries of each directory (or the file itself) last modified at
/// or before the retention window, and deletes them only with <c>--confirm</c>.
/// </summary>
internal static class PurgeReportingRetention
{
    private static readonly StringComparer PathOrder = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>The entries modified at or before <paramref name="now"/> minus the retention days, sorted by path.</summary>
    public static List<PurgeCandidate> DiscoverCandidates(IReadOnlyList<string> roots, int retentionDays, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentOutOfRangeException.ThrowIfNegative(retentionDays);
        var threshold = now - TimeSpan.FromDays(retentionDays);
        var candidates = new List<PurgeCandidate>();
        foreach (var given in roots)
        {
            var root = Path.GetFullPath(RunProfileStore.Expand(given));
            IEnumerable<string> entries = Directory.Exists(root) ? Directory.EnumerateFileSystemEntries(root)
                : File.Exists(root) ? [root]
                : [];
            foreach (var entry in entries)
            {
                if (Modified(entry) is { } modified && modified <= threshold)
                {
                    candidates.Add(new PurgeCandidate(entry, (now - modified).TotalDays));
                }
            }
        }

        return [.. candidates.OrderBy(candidate => candidate.Path, PathOrder)];
    }

    /// <summary>The deleted paths, none unless <paramref name="confirm"/>. A link is removed itself, never what it points to.</summary>
    public static List<string> Purge(IEnumerable<PurgeCandidate> candidates, bool confirm)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (!confirm)
        {
            return [];
        }

        var deleted = new List<string>();
        foreach (var candidate in candidates)
        {
            FileSystemInfo info = Directory.Exists(candidate.Path) ? new DirectoryInfo(candidate.Path) : new FileInfo(candidate.Path);
            if (info is DirectoryInfo directory && info.LinkTarget is null)
            {
                directory.Delete(recursive: true);
            }
            else
            {
                info.Delete();
            }

            deleted.Add(candidate.Path);
        }

        return deleted;
    }

    public static int Run(IReadOnlyList<string> paths, int retentionDays, bool confirm, TextWriter stdout, TimeProvider? time = null)
    {
        var candidates = DiscoverCandidates(paths, retentionDays, (time ?? TimeProvider.System).GetUtcNow());
        if (candidates.Count == 0)
        {
            ConsoleText.Print(stdout, "No purge candidates found within retention policy.");
            return 0;
        }

        ConsoleText.Print(stdout, "Candidates:");
        foreach (var candidate in candidates)
        {
            ConsoleText.Print(stdout, string.Create(CultureInfo.InvariantCulture, $" - {candidate.Path} (age={candidate.AgeDays:0.0}d)"));
        }

        ConsoleText.Print(
            stdout,
            confirm ? $"Deleted {Purge(candidates, confirm: true).Count} item(s)." : "Dry run complete. Re-run with --confirm to delete candidates.");
        return 0;
    }

    // The last write time, of the target for a link; null when the entry (or the link's target) is gone.
    private static DateTimeOffset? Modified(string entry)
    {
        FileSystemInfo info = Directory.Exists(entry) ? new DirectoryInfo(entry) : new FileInfo(entry);
        if (info.LinkTarget is not null)
        {
            info = info.ResolveLinkTarget(returnFinalTarget: true) ?? info;
        }

        return info.Exists ? new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero) : null;
    }
}
