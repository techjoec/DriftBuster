using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Detection;

/// <summary>
/// A detector for walks that must survive single bad files (multi-server, the console scan): a file that cannot be read
/// or looked up is recorded in <see cref="SkippedFiles"/>, reported to <see cref="OnSkipped"/>, and the walk continues. An error on the root being scanned (it
/// cannot be looked up, read or listed) still raises. <see cref="CancellationToken"/> is checked before every entry is
/// looked up and while the tree is walked.
/// </summary>
public sealed class SkippingDetector(int? sampleSize, long maxTotalSampleBytes, Action<string>? onWarning = null)
    : Detector(sampleSize: sampleSize, maxTotalSampleBytes: maxTotalSampleBytes, onWarning: onWarning)
{
    private string? _root;

    public IList<string> SkippedFiles { get; } = new List<string>();

    public Action<string, DetectorIOException>? OnSkipped { get; set; }

    public CancellationToken CancellationToken { get; set; }

    public override IReadOnlyList<(string Path, DetectionMatch? Match)> ScanPath(string root, string glob = "**/*", bool resetBudget = true)
    {
        ArgumentNullException.ThrowIfNull(root);
        _root = LexicalPath.Str(root);
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
        if (_root is null || string.Equals(LexicalPath.Str(path), _root, StringComparison.Ordinal))
        {
            base.HandleError(path, error);
        }

        SkippedFiles.Add(path);
        OnSkipped?.Invoke(path, error);
    }

    protected internal override bool IsFile(string path)
    {
        CancellationToken.ThrowIfCancellationRequested();
        return base.IsFile(path);
    }

    protected internal override IReadOnlyList<string> EnumerateFiles(string root, string glob)
        => FilePaths.SortedGlob(root, glob, CancellationToken).Select(Path.GetFullPath).ToList();
}
