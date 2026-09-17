using DriftBuster.Backend.Detection;
using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.MultiServer;

/// <summary>
/// The multi-server detector: a file that cannot be read or looked up is recorded in <see cref="SkippedFiles"/>
/// and the walk continues. An error on the root being scanned (it
/// cannot be looked up, read or listed) still raises. <see cref="CancellationToken"/> is checked before every entry is
/// looked up and while the tree is walked.
/// </summary>
internal sealed class SkippingDetector(int? sampleSize, long maxTotalSampleBytes)
    : Detector(sampleSize: sampleSize, maxTotalSampleBytes: maxTotalSampleBytes)
{
    private string? _root;

    public List<string> SkippedFiles { get; } = [];

    public CancellationToken CancellationToken { get; set; }

    public override IReadOnlyList<(string Path, DetectionMatch? Match)> ScanPath(string root, string glob = "**/*", bool resetBudget = true)
    {
        ArgumentNullException.ThrowIfNull(root);
        _root = EnginePurePath.Str(root);
        try
        {
            return base.ScanPath(root, glob, resetBudget);
        }
        finally
        {
            _root = null;
        }
    }

    protected internal override void HandleError(string path, DetectorIOException error)
    {
        if (_root is null || string.Equals(EnginePurePath.Str(path), _root, StringComparison.Ordinal))
        {
            base.HandleError(path, error);
        }

        SkippedFiles.Add(path);
    }

    protected internal override bool IsFile(string path)
    {
        CancellationToken.ThrowIfCancellationRequested();
        return base.IsFile(path);
    }

    protected internal override IReadOnlyList<string> EnumerateFiles(string root, string glob)
        => EnginePath.SortedGlob(root, glob, CancellationToken).Select(EnginePath.Absolute).ToList();
}
