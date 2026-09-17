using DriftBuster.Cli.Commands;

namespace DriftBuster.Cli.Tests;

/// <summary>Mirror of tests/scripts/test_purge_reporting_retention.py through <see cref="PurgeReportingRetention"/>.</summary>
public sealed class PurgeReportingRetentionTests : IDisposable
{
    private readonly DirectoryInfo _tmp = Directory.CreateTempSubdirectory("driftbuster-purge-");

    public void Dispose() => _tmp.Delete(recursive: true);

    private static void SetMtime(string path, int daysAgo, DateTimeOffset? reference = null)
    {
        var mtime = (reference ?? DateTimeOffset.UtcNow).UtcDateTime.AddDays(-daysAgo);
        if (Directory.Exists(path))
        {
            Directory.SetLastWriteTimeUtc(path, mtime);
        }
        else
        {
            File.SetLastWriteTimeUtc(path, mtime);
        }
    }

    [Fact]
    public void DiscoverCandidatesFiltersByRetention()
    {
        var root = Directory.CreateDirectory(Path.Combine(_tmp.FullName, "captures")).FullName;
        var oldDir = Directory.CreateDirectory(Path.Combine(root, "2024-10-10")).FullName;
        var newDir = Directory.CreateDirectory(Path.Combine(root, "2025-11-01")).FullName;
        var now = new DateTimeOffset(2025, 11, 13, 0, 0, 0, TimeSpan.Zero);
        SetMtime(oldDir, 60, now);
        SetMtime(newDir, 5, now);

        var candidates = PurgeReportingRetention.DiscoverCandidates([root], 30, now);

        candidates.Select(candidate => candidate.Path).Should().Equal(oldDir);
        candidates[0].AgeDays.Should().BeGreaterThanOrEqualTo(59);
    }

    [Fact]
    public void DiscoverCandidatesIgnoresMissingPaths()
    {
        var missing = Path.Combine(_tmp.FullName, "missing");
        var existing = Directory.CreateDirectory(Path.Combine(_tmp.FullName, "artifacts")).FullName;
        var staleFile = Path.Combine(existing, "report.txt");
        File.WriteAllText(staleFile, "placeholder");
        var now = new DateTimeOffset(2025, 11, 13, 0, 0, 0, TimeSpan.Zero);
        SetMtime(staleFile, 45, now);

        var candidates = PurgeReportingRetention.DiscoverCandidates([missing, existing], 30, now);

        candidates.Select(candidate => candidate.Path).Should().Equal(staleFile);
    }

    [Fact]
    public void PurgeDeletesWhenConfirmed()
    {
        var targetDir = Directory.CreateDirectory(Path.Combine(_tmp.FullName, "to_purge")).FullName;
        File.WriteAllText(Path.Combine(targetDir, "evidence.json"), "{}");
        SetMtime(targetDir, 40);

        var deleted = PurgeReportingRetention.Purge([new PurgeCandidate(targetDir, 40)], confirm: true);

        deleted.Should().Equal(targetDir);
        Directory.Exists(targetDir).Should().BeFalse();
    }

    [Fact]
    public void PurgeDoesNotDeleteWhenNotConfirmed()
    {
        var keepDir = Directory.CreateDirectory(Path.Combine(_tmp.FullName, "keep")).FullName;
        SetMtime(keepDir, 50);

        PurgeReportingRetention.Purge([new PurgeCandidate(keepDir, 50)], confirm: false);

        Directory.Exists(keepDir).Should().BeTrue();
    }
}
