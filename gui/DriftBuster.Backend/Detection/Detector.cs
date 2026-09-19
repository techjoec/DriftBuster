using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
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
            throw new ArgumentOutOfRangeException(nameof(sampleSize), "sample_size must be a positive integer.");
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
            throw new ArgumentOutOfRangeException(nameof(value), "max_total_sample_bytes must be a positive integer.");
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
    protected internal virtual Stream OpenFile(string path) => new FileStream(EnginePath.KernelPath(path), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    /// <summary><see cref="EnginePath.IsFile"/>; overridable for fault injection.</summary>
    protected internal virtual bool IsFile(string path) => EnginePath.IsFile(path);

    internal static string? ResolvePhysicalPath(string fullPath) => EnginePath.ResolvePhysicalPath(fullPath);

    /// <summary>Absolute paths (".." kept) of the sorted glob matches under <paramref name="root"/>; overridable for fault injection.</summary>
    protected internal virtual IReadOnlyList<string> EnumerateFiles(string root, string glob)
        => EnginePath.SortedGlob(root, glob).Select(EnginePath.Absolute).ToList();

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
    // other file, so a match timeout is reported the way an unreadable file is; this never
    // fires on a sample the engine matches in bounded time.
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
        var metadata = match.Metadata;
        metadata.TryAdd("bytes_sampled", sample.Length);

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

        DetectionMetadata.ValidateDetectionMetadata(match, DetectionCatalog.Default);
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
    /// Scans a file or directory within the aggregate sampling budget. Only regular files are scanned; an undecodable name and
    /// a missing root are reported through <see cref="HandleError"/>. A walk stops after the file that exhausts the budget.
    /// </summary>
    /// <param name="resetBudget">Reset the aggregate counter first; false continues a budget across roots.</param>
    public virtual IReadOnlyList<(string Path, DetectionMatch? Match)> ScanPath(string root, string glob = "**/*", bool resetBudget = true)
    {
        ArgumentNullException.ThrowIfNull(root);
        // Normalised so "x.config/" names the file.
        root = LexicalPath.Str(root);
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

            if (!Directory.Exists(EnginePath.KernelPath(root)))
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
                    if (EnginePath.IsUndecodableName(path))
                    {
                        // The runtime cannot name the file, so the entry is reported.
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
    /// Scans <paramref name="root"/> and adds the profile configs that apply to each result (matched on the path relative to the
    /// root, or the file name). A config whose metadata sets <c>ignore_review_flags</c> clears <c>needs_review</c> and sets
    /// <c>review_ignored</c>; an error reading the configs counts as not ignoring.
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
            throw new ArgumentNullException(nameof(profileStore), "profile_store must be provided.");
        }

        var normalizedTags = (tags ?? []).OfType<string>().Select(tag => tag.Trim()).Where(tag => tag.Length > 0).ToHashSet(StringComparer.Ordinal);
        var scanResults = ScanPath(root, glob);
        var profiled = new List<ProfiledDetection>();
        var rootIsDir = Directory.Exists(EnginePath.KernelPath(root));

        foreach (var (path, detection) in scanResults)
        {
            var relative = rootIsDir ? LexicalPath.RelativeTo(path, root) ?? PathText.Name(path) : PathText.Name(path);
            var applied = profileStore.MatchingConfigs(normalizedTags, relative);
            if (detection?.Metadata is { } metadata && applied.Any(cfg => cfg.Config.IgnoreReviewFlags))
            {
                if (metadata["needs_review"]?.GetValueKind() == JsonValueKind.True)
                {
                    metadata["review_ignored"] = true;
                    metadata["needs_review"] = false;
                }
            }

            profiled.Add(new ProfiledDetection(path, detection, applied));
        }

        return profiled;
    }

    /// <summary>Convenience wrapper for a single file: a fresh detector per call.</summary>
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

    /// <summary>Convenience wrapper for a tree: a fresh detector per call.</summary>
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

    // Upper-cases the first letter (any L* code point, astral included) with full case mapping (EngineText.Upper).
    private static string TitleiseComponent(string component)
    {
        if (component.Length == 0)
        {
            return component;
        }

        var offset = 0;
        foreach (var rune in component.EnumerateRunes())
        {
            if (EngineUnicode.IsAlpha(rune.Value))
            {
                return string.Concat(component.AsSpan(0, offset), EngineText.Upper(rune), component.AsSpan(offset + rune.Utf16SequenceLength));
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
            var text = EngineText.Strip(raw ?? string.Empty);
            if (text.Length == 0)
            {
                continue;
            }

            var words = EngineText.Split(text);
            var formatted = string.Join(' ', words.Select(NormaliseReasonToken));
            if (seen.Add(formatted))
            {
                normalised.Add(formatted);
            }
        }

        return normalised;
    }
}
