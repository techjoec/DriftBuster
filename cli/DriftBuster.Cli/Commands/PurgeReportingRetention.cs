using System.Numerics;

using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Reporting;

namespace DriftBuster.Cli.Commands;

/// <summary>
/// <c>driftbuster maint purge-reporting-retention PATH...</c>: lists the entries of each
/// directory (or the file itself) last modified at or before the retention window, and deletes them only with <c>--confirm</c>.
/// </summary>
internal static partial class PurgeReportingRetention
{
    private const long MicrosecondsPerDay = 86_400_000_000;

    // datetime.min (0001-01-01T00:00:00) in microseconds from the Unix epoch.
    private static readonly BigInteger MinMicroseconds = (DateTime.MinValue.Ticks - DateTime.UnixEpoch.Ticks) / TimeSpan.TicksPerMicrosecond;

    /// <summary><c>discover_candidates(roots, retention_days=..., now=...)</c>, sorted by path.</summary>
    public static List<PurgeCandidate> DiscoverCandidates(IReadOnlyList<string> roots, BigInteger retentionDays, DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(roots);
        if (retentionDays.Sign < 0)
        {
            throw new EngineValueException("retention_days must be non-negative", nameof(retentionDays));
        }

        var clock = MicrosecondsOf((now ?? DateTimeOffset.UtcNow).UtcTicks);
        var threshold = clock - EngineTimeDelta.FromMicroseconds(retentionDays * MicrosecondsPerDay).TotalMicroseconds;
        if (threshold < MinMicroseconds)
        {
            throw new OverflowException("date value out of range");
        }

        var candidates = new List<PurgeCandidate>();
        foreach (var given in roots)
        {
            var root = EnginePath.Resolve(EnginePath.ExpandUser(EnginePurePath.Str(given)));
            if (!TextModeFile.Exists(root))
            {
                continue;
            }

            var entries = Directory.Exists(root)
                ? Directory.EnumerateFileSystemEntries(root).Select(entry => EnginePurePath.Join(root, Path.GetFileName(entry)))
                : [root];
            foreach (var entry in entries)
            {
                if (ModifiedMicroseconds(entry) is { } modified && modified <= threshold)
                {
                    var age = EngineTimeDelta.FromMicroseconds(clock - modified).TotalSeconds() / 86400;
                    candidates.Add(new PurgeCandidate(entry, age));
                }
            }
        }

        candidates.Sort((left, right) => ComparePaths(left.Path, right.Path));
        return candidates;
    }

    /// <summary><c>purge(candidates, confirm=...)</c>: the deleted paths, none unless <paramref name="confirm"/>.</summary>
    public static List<string> Purge(IEnumerable<PurgeCandidate> candidates, bool confirm)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var deleted = new List<string>();
        foreach (var candidate in candidates)
        {
            if (!confirm)
            {
                continue;
            }

            if (Directory.Exists(candidate.Path))
            {
                DeleteContents(candidate.Path);
                RemoveDirectory(candidate.Path);
            }
            else
            {
                File.Delete(candidate.Path);
            }

            deleted.Add(candidate.Path);
        }

        return deleted;
    }

    public static int Run(IReadOnlyList<string> paths, BigInteger retentionDays, bool confirm, TextWriter stdout)
    {
        var candidates = DiscoverCandidates(paths, retentionDays);
        if (candidates.Count == 0)
        {
            ConsoleText.Print(stdout, "No purge candidates found within retention policy.");
            return 0;
        }

        ConsoleText.Print(stdout, "Candidates:");
        foreach (var candidate in candidates)
        {
            ConsoleText.Print(stdout, $" - {candidate.Path} (age={ReportValues.FormatFixed(candidate.AgeDays, 1)}d)");
        }

        ConsoleText.Print(
            stdout,
            confirm ? $"Deleted {Purge(candidates, confirm: true).Count} item(s)." : "Dry run complete. Re-run with --confirm to delete candidates.");
        return 0;
    }
}
