using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

using DriftBuster.Backend.Detection.Catalog;
using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Profiles.Detection;

namespace DriftBuster.Backend.Detection;

/// <summary>Coordinates format plugins to detect configuration types under bounded sampling and an aggregate budget.</summary>
public class Detector
{
    /// <summary>Bytes read from each file when no sample size is requested.</summary>
    public const int DefaultSampleSize = 128 * 1024;

    /// <summary>Guardrail against excessive reads; larger requests are clamped with a warning.</summary>
    public const int MaxSampleSize = 512 * 1024;

    /// <summary>Aggregate sampling guardrail applied when no budget is requested.</summary>
    public const long DefaultTotalSampleBudget = 16L * 1024 * 1024;

    private readonly List<IFormatPlugin> _plugins;
    private readonly int _sampleSize;
    private readonly long _maxTotalSampleBytes;
    private readonly Action<string, Exception>? _onError;
    private readonly Action<string> _warn;
    private long _consumedSampleBytes;
    private bool _budgetExhausted;

    /// <param name="plugins">Explicit plugin sequence; null uses <see cref="DefaultPlugins"/>.</param>
    /// <param name="sampleSize">Bytes read from each file; null uses <see cref="DefaultSampleSize"/>.</param>
    /// <param name="maxTotalSampleBytes">Aggregate sampling budget; null uses <see cref="DefaultTotalSampleBudget"/>.</param>
    /// <param name="sortPlugins">Re-sort plugins by priority (stable); disable to keep the given order.</param>
    /// <param name="onError">Invoked with (path, <see cref="DetectorIOException"/>) before the error is raised.</param>
    /// <param name="onWarning">Receives guardrail warnings; defaults to <see cref="Trace.TraceWarning(string)"/>.</param>
    public Detector(
        IEnumerable<IFormatPlugin>? plugins = null,
        int? sampleSize = null,
        long? maxTotalSampleBytes = null,
        bool sortPlugins = true,
        Action<string, Exception>? onError = null,
        Action<string>? onWarning = null)
    {
        _warn = onWarning ?? (message => Trace.TraceWarning(message));
        var selected = plugins is null ? DefaultPlugins.GetPlugins().ToList() : plugins.ToList();
        if (sortPlugins)
        {
            selected = selected.OrderBy(plugin => plugin.Priority).ToList();
        }

        _plugins = selected;
        _sampleSize = ValidateSampleSize(sampleSize ?? DefaultSampleSize);
        _maxTotalSampleBytes = ValidateTotalSampleBudget(maxTotalSampleBytes) ?? DefaultTotalSampleBudget;
        _consumedSampleBytes = 0;
        _budgetExhausted = false;
        _onError = onError;
    }

    /// <summary>The plugins in the order they are consulted.</summary>
    public IReadOnlyList<IFormatPlugin> Plugins => _plugins;

    /// <summary>The effective (clamped) per-file sample size.</summary>
    public int SampleSize => _sampleSize;

    public bool SampleBudgetExhausted => _budgetExhausted;

    public long SampleBudgetRemaining => Math.Max(0, _maxTotalSampleBytes - _consumedSampleBytes);

    /// <summary>Resets the aggregate sampling counters for a fresh scan.</summary>
    public void ResetSampleBudget()
    {
        _consumedSampleBytes = 0;
        _budgetExhausted = false;
    }

    private int ValidateSampleSize(int sampleSize)
    {
        if (sampleSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleSize), sampleSize, "sample_size must be a positive integer");
        }

        if (sampleSize > MaxSampleSize)
        {
            _warn(string.Format(
                CultureInfo.InvariantCulture,
                "Sample size {0} exceeds {1} bytes; clamping to guardrail.",
                sampleSize,
                MaxSampleSize));
            return MaxSampleSize;
        }

        return sampleSize;
    }

    private static long? ValidateTotalSampleBudget(long? value)
    {
        if (value is null)
        {
            return null;
        }

        if (value <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "max_total_sample_bytes must be a positive integer");
        }

        return value;
    }

    /// <summary>
    /// Reports <paramref name="error"/> to the error callback and raises it. Subclasses may override to swallow
    /// errors, in which case the scan continues past the failing path.
    /// </summary>
    protected internal virtual void HandleError(string path, DetectorIOException error)
    {
        ArgumentNullException.ThrowIfNull(error);
        if (_onError is not null)
        {
            try
            {
                _onError(path, error);
            }
            catch (Exception exc) when (exc is not OutOfMemoryException)
            {
                _warn($"on_error handler raised during scan: {exc}");
            }
        }

        throw error;
    }

    /// <summary>Opens the file whose first bytes are sampled; overridable for fault injection.</summary>
    protected internal virtual Stream OpenFile(string path) => new FileStream(PythonPath.KernelPath(path), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    /// <summary><see cref="PythonPath.IsFile"/>; overridable for fault injection.</summary>
    protected internal virtual bool IsFile(string path) => PythonPath.IsFile(path);

    /// <summary><see cref="PythonPath.ResolvePhysicalPath"/>.</summary>
    internal static string? ResolvePhysicalPath(string fullPath) => PythonPath.ResolvePhysicalPath(fullPath);

    /// <summary>
    /// The absolute paths (<see cref="PythonPath.Absolute"/>, ".." parts kept) of <c>sorted(root.glob(glob))</c>
    /// (<see cref="PythonPath.SortedGlob"/>); overridable for fault injection.
    /// </summary>
    protected internal virtual IReadOnlyList<string> EnumerateFiles(string root, string glob)
        => PythonPath.SortedGlob(root, glob).Select(PythonPath.Absolute).ToList();

    private byte[] ReadSample(string path, int readSize)
    {
        using var stream = OpenFile(path);
        var buffer = new byte[readSize];
        var total = 0;
        while (total < readSize)
        {
            var read = stream.Read(buffer, total, readSize - total);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total == readSize ? buffer : buffer[..total];
    }

    /// <summary>
    /// Samples <paramref name="path"/> and returns the first plugin match, enriched with sampling metadata, validated
    /// against the catalog and with normalised reasons; null when no plugin matched or the budget was already spent.
    /// </summary>
    public DetectionMatch? ScanFile(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!IsFile(path))
        {
            throw new FileNotFoundException($"Expected file path, got: {path}", path);
        }

        if (_consumedSampleBytes >= _maxTotalSampleBytes)
        {
            _budgetExhausted = true;
            _warn($"Sample budget exhausted before scanning: {path}");
            return null;
        }

        var remaining = _maxTotalSampleBytes - _consumedSampleBytes;
        var readSize = (int)Math.Min(_sampleSize + 1L, remaining + 1);
        byte[] raw;
        try
        {
            raw = ReadSample(path, readSize);
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
        {
            HandleError(path, new DetectorIOException(path, exc.Message, exc));
            return null;
        }

        var sampleLength = (int)Math.Min(Math.Min(_sampleSize, remaining), raw.Length);
        var sample = sampleLength == raw.Length ? raw : raw[..sampleLength];
        var truncated = raw.Length > _sampleSize;
        string? text = null;
        string? encoding = null;
        if (FormatRegistry.LooksText(sample))
        {
            (text, encoding) = FormatRegistry.DecodeText(sample);
        }

        _consumedSampleBytes += sample.Length;
        if (_consumedSampleBytes >= _maxTotalSampleBytes)
        {
            _budgetExhausted = true;
        }

        var first = FirstMatch(path, sample, text);
        return first is null ? null : Enrich(first, sample, encoding, truncated);
    }

    // First plugin match in registry order. A pattern that gives up on this sample must not abort the scan of every
    // other file, so a match timeout is reported the way an unreadable file is; Python has no match timeouts, and
    // this never fires on a sample the port matches in bounded time.
    private DetectionMatch? FirstMatch(string path, byte[] sample, string? text)
    {
        foreach (var plugin in _plugins)
        {
            DetectionMatch? match;
            try
            {
                match = plugin.Detect(path, sample, text);
            }
            catch (RegexMatchTimeoutException exc)
            {
                HandleError(path, new DetectorIOException(path, $"{plugin.Name} plugin timed out matching the sample", exc));
                return null;
            }

            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

    private DetectionMatch Enrich(DetectionMatch match, byte[] sample, string? encoding, bool truncated)
    {
        var metadata = DetectionMetadata.EnsureMapping(match.Metadata);
        if (!metadata.ContainsKey("bytes_sampled"))
        {
            metadata["bytes_sampled"] = sample.Length;
        }

        if (encoding is not null && !metadata.ContainsKey("encoding"))
        {
            metadata["encoding"] = encoding;
            AppendReason(match, $"Decoded content using {encoding} encoding");
        }

        if (truncated)
        {
            metadata["sample_truncated"] = true;
            AppendReason(match, string.Format(CultureInfo.InvariantCulture, "Truncated sample to {0}B", _sampleSize));
        }

        if (_budgetExhausted)
        {
            metadata["sample_budget_exhausted"] = true;
            AppendReason(match, string.Format(CultureInfo.InvariantCulture, "Sampling budget exhausted after {0}B", sample.Length));
        }

        match.Metadata = metadata.Count > 0 ? metadata : null;
        match.Metadata = DetectionMetadata.ValidateDetectionMetadata(match, DetectionCatalog.Default);
        match.Reasons = NormaliseReasons(match.Reasons);
        return match;
    }

    private static void AppendReason(DetectionMatch match, string reason)
    {
        if (!match.Reasons.Contains(reason))
        {
            match.Reasons.Add(reason);
        }
    }

    /// <summary>
    /// Scans a file or directory while enforcing the aggregate sampling budget. The root is spelled as <c>Path(root)</c>
    /// spells it (<see cref="PythonPurePath.Str"/>). Only regular files are scanned (<see cref="PythonPath.IsFile"/>); an entry
    /// whose name the runtime cannot decode is reported through <see cref="HandleError"/>. A file root yields a single entry;
    /// a missing root raises <see cref="DetectorIOException"/> through <see cref="HandleError"/>; a directory walk
    /// stops after the first file that exhausts the budget.
    /// </summary>
    /// <param name="resetBudget">Reset the aggregate counter before scanning; false continues an existing budget across roots.</param>
    public virtual IReadOnlyList<(string Path, DetectionMatch? Match)> ScanPath(string root, string glob = "**/*", bool resetBudget = true)
    {
        ArgumentNullException.ThrowIfNull(root);
        // root = Path(root): a trailing separator, "//" and "." parts are dropped, so "x.config/" names the file.
        root = PythonPurePath.Str(root);
        var results = new List<(string Path, DetectionMatch? Match)>();
        try
        {
            if (IsFile(root))
            {
                if (resetBudget)
                {
                    ResetSampleBudget();
                }

                results.Add((root, ScanFile(root)));
                return results;
            }

            if (!Directory.Exists(PythonPath.KernelPath(root)))
            {
                throw new FileNotFoundException($"Path does not exist: {root}", root);
            }
        }
        catch (Exception exc) when (exc is IOException and not DetectorIOException || exc is UnauthorizedAccessException)
        {
            // A DetectorIOException raised by ScanFile has already been reported through HandleError; it propagates as is.
            HandleError(root, new DetectorIOException(root, exc.Message, exc));
            return results;
        }

        if (resetBudget)
        {
            ResetSampleBudget();
        }

        IReadOnlyList<string> candidates;
        try
        {
            candidates = EnumerateFiles(root, glob);
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
        {
            HandleError(root, new DetectorIOException(root, exc.Message, exc));
            return results;
        }

        ScanCandidates(root, candidates, results);
        return results;
    }

    private void ScanCandidates(string root, IReadOnlyList<string> candidates, List<(string Path, DetectionMatch? Match)> results)
    {
        foreach (var path in candidates)
        {
            try
            {
                if (!IsFile(path))
                {
                    if (PythonPath.IsUndecodableName(path))
                    {
                        // Python opens the name through surrogateescape; the port cannot name the file, so it reports the entry.
                        HandleError(path, new DetectorIOException(path, "File name is not valid UTF-8; the entry cannot be opened"));
                    }

                    continue;
                }

                results.Add((path, ScanFile(path)));
                if (_budgetExhausted)
                {
                    _warn($"Sample budget exhausted while scanning {path}; skipping remaining paths under {root}");
                    break;
                }
            }
            catch (Exception exc) when (exc is IOException and not DetectorIOException || exc is UnauthorizedAccessException)
            {
                HandleError(path, new DetectorIOException(path, exc.Message, exc));
            }
        }
    }

    /// <summary>
    /// <c>scan_with_profiles</c>: scans <paramref name="root"/> and annotates every result with the profile configs that apply to
    /// its path (<c>path.relative_to(root).as_posix()</c> under a directory root, falling back to the file name when the path is
    /// not under it; the bare file name for a file root). When any applicable config's metadata sets a truthy
    /// <c>ignore_review_flags</c>, a detection whose <c>needs_review</c> is truthy gets <c>review_ignored</c> true and
    /// <c>needs_review</c> false; an exception while reading the applied configs counts as not ignoring.
    /// </summary>
    public IReadOnlyList<ProfiledDetection> ScanWithProfiles(
        string root,
        IProfileMatcher profileStore,
        IEnumerable<string?>? tags = null,
        string glob = "**/*")
    {
        ArgumentNullException.ThrowIfNull(root);
        if (profileStore is null)
        {
            throw new PythonValueException("profile_store must be provided", nameof(profileStore));
        }

        var normalizedTags = ProfileTags.Normalize(tags);
        var scanResults = ScanPath(root, glob);
        var profiled = new List<ProfiledDetection>();
        var rootIsDir = Directory.Exists(PythonPath.KernelPath(root));

        foreach (var (path, detection) in scanResults)
        {
            var relative = rootIsDir ? PythonPurePath.RelativeTo(path, root) ?? PathText.Name(path) : PathText.Name(path);
            var applied = profileStore.MatchingConfigs(normalizedTags, relative);
            if (detection?.Metadata is { Count: > 0 } metadata)
            {
                bool ignore;
                try
                {
                    ignore = applied.Any(cfg => PythonBuiltins.IsTruthy(cfg.Config.Metadata.TryGetValue("ignore_review_flags", out var flag) ? flag : null));
                }
                catch (Exception exc) when (exc is not OutOfMemoryException)
                {
                    ignore = false;
                }

                if (ignore && metadata.TryGetValue("needs_review", out var needsReview) && PythonBuiltins.IsTruthy(needsReview))
                {
                    metadata["review_ignored"] = true;
                    metadata["needs_review"] = false;
                }
            }

            profiled.Add(new ProfiledDetection(path, detection, applied));
        }

        return profiled;
    }

    /// <summary>Convenience wrapper mirroring the module-level <c>scan_file</c>: a fresh detector per call.</summary>
    public static DetectionMatch? ScanFileWithDefaults(
        string path,
        int? sampleSize = null,
        IEnumerable<IFormatPlugin>? plugins = null,
        bool sortPlugins = true,
        Action<string, Exception>? onError = null)
    {
        var detector = new Detector(plugins, sampleSize, sortPlugins: sortPlugins, onError: onError);
        return detector.ScanFile(path);
    }

    /// <summary>Convenience wrapper mirroring the module-level <c>scan_path</c>: a fresh detector per call.</summary>
    public static IReadOnlyList<(string Path, DetectionMatch? Match)> ScanPathWithDefaults(
        string root,
        string glob = "**/*",
        int? sampleSize = null,
        IEnumerable<IFormatPlugin>? plugins = null,
        bool sortPlugins = true,
        Action<string, Exception>? onError = null)
    {
        var detector = new Detector(plugins, sampleSize, sortPlugins: sortPlugins, onError: onError);
        return detector.ScanPath(root, glob);
    }

    // Upper-cases the first letter (any L* code point, astral included) with Python's full str.upper() mapping.
    private static string TitleiseComponent(string component)
    {
        if (component.Length == 0)
        {
            return component;
        }

        var offset = 0;
        foreach (var rune in component.EnumerateRunes())
        {
            if (PythonUnicode.IsAlpha(rune.Value))
            {
                return string.Concat(component.AsSpan(0, offset), PythonText.Upper(rune), component.AsSpan(offset + rune.Utf16SequenceLength));
            }

            offset += rune.Utf16SequenceLength;
        }

        return component;
    }

    private static string NormaliseReasonToken(string token)
    {
        var parts = token.Split('-');
        for (var index = 0; index < parts.Length; index++)
        {
            var subparts = parts[index].Split(':');
            for (var sub = 0; sub < subparts.Length; sub++)
            {
                subparts[sub] = TitleiseComponent(subparts[sub]);
            }

            parts[index] = string.Join(':', subparts);
        }

        return string.Join('-', parts);
    }

    /// <summary>
    /// Trims, collapses whitespace, title-cases the first letter of every '-' and ':' sub-part of each token and
    /// drops empty or duplicate reasons while preserving order.
    /// </summary>
    internal static IList<string> NormaliseReasons(IEnumerable<string> reasons)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var normalised = new List<string>();
        foreach (var raw in reasons)
        {
            var text = PythonText.Strip(raw ?? string.Empty);
            if (text.Length == 0)
            {
                continue;
            }

            var words = PythonText.Split(text);
            var formatted = string.Join(' ', words.Select(NormaliseReasonToken));
            if (seen.Add(formatted))
            {
                normalised.Add(formatted);
            }
        }

        return normalised;
    }
}
