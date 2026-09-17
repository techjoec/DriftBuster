using DriftBuster.Backend.Detection;
using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Hunt;

/// <summary>Searches file trees for dynamic configuration values.</summary>
public static partial class HuntEngine
{
    /// <summary>Bytes read from each file.</summary>
    public const int DefaultSampleSize = 128 * 1024;

    /// <summary>The default <c>"{{{{ {token_name} }}}}"</c>, which formats to <c>{{ name }}</c>.</summary>
    public const string DefaultPlaceholderTemplate = "{{{{ {token_name} }}}}";

    /// <summary>Test seam for <see cref="LexicalPath.RelativeTo"/>: null when the path does not lie under the root directory.</summary>
    internal static Func<string, string, string?> RelativeTo { get; set; } = LexicalPath.RelativeTo;

    /// <summary>
    /// Searches a root for the given rules. A file root is scanned alone; a directory root is walked with
    /// <paramref name="glob"/> (<see cref="EnginePath.SortedGlob"/>, symlinked directories not followed). A file is
    /// excluded when an exclusion pattern matches it (<see cref="ShouldExclude"/>).
    /// </summary>
    /// <remarks>
    /// A file that cannot be opened, read or looked up (as for a file inside a directory that cannot be searched) is
    /// skipped and listed in <see cref="HuntScanResult.UnreadableFiles"/>, as is an entry whose file name the runtime
    /// cannot decode (<see cref="EnginePath.IsUndecodableName"/>). Only regular files are read
    /// (<see cref="EnginePath.IsFile"/>): a FIFO, socket or device is skipped.
    /// <paramref name="cancellationToken"/> is honoured while the tree is walked, between files and between pattern match
    /// attempts; one attempt is bounded by <see cref="PatternRegex.AttemptTimeout"/>, so cancellation is observed within
    /// that limit even for a pattern that backtracks badly, and such a pattern is abandoned rather than counted as an
    /// unreadable file. A root that does not exist yields no hits; a directory root that cannot be listed raises its I/O
    /// error. An empty or rooted <paramref name="glob"/> raises <see cref="ArgumentException"/>.
    /// </remarks>
    public static HuntScanResult HuntPath(
        string root,
        IReadOnlyList<HuntRule> rules,
        string glob = "**/*",
        long sampleSize = DefaultSampleSize,
        IReadOnlyList<string>? excludePatterns = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(glob);
        var (targets, rootDirectory) = Targets(LexicalPath.Str(root), glob, cancellationToken);
        var exclusions = excludePatterns ?? [];
        var hits = new List<HuntFinding>();
        var unreadable = new List<string>();
        foreach (var candidate in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (exclusions.Count > 0)
            {
                var relative = RelativeTo(candidate, rootDirectory);
                if (ShouldExclude(candidate, relative, exclusions))
                {
                    continue;
                }
            }

            try
            {
                if (!EnginePath.IsFile(candidate))
                {
                    // An entry whose name the runtime cannot decode (Targets keeps it): it cannot be opened.
                    unreadable.Add(candidate);
                    continue;
                }

                var text = ReadText(candidate, sampleSize);
                if (text is null)
                {
                    continue;
                }

                var fileHits = new List<HuntFinding>();
                string? lowered = null;
                foreach (var rule in rules)
                {
                    if (rule.Keywords.Count > 0)
                    {
                        lowered ??= EngineText.Lower(text);
                        if (!rule.Keywords.All(keyword => EngineText.Contains(lowered, keyword)))
                        {
                            continue;
                        }
                    }

                    fileHits.AddRange(ExtractHits(text, rule, candidate, cancellationToken));
                }

                hits.AddRange(fileHits);
            }
            catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
            {
                unreadable.Add(candidate);
            }
        }

        return new HuntScanResult(hits, rootDirectory, unreadable);
    }

    // path = Path(root) (the caller passes the spelling PurePath gives it); targets = [path] if path.is_file() else
    // [c for c in path.glob(glob) if c.is_file()]; root_dir = path if path.is_dir() else path.parent. A missing root globs to
    // nothing. Entries whose names the runtime cannot decode are kept, in walk order, so HuntPath reports them.
    private static (IReadOnlyList<string> Targets, string RootDirectory) Targets(string root, string glob, CancellationToken cancellationToken)
    {
        if (EnginePath.IsFile(root))
        {
            return ([root], LexicalPath.Parent(root));
        }

        var targets = EnginePath.SortedGlob(root, glob, cancellationToken).Where(IsTarget).ToList();
        return (targets, Directory.Exists(EnginePath.KernelPath(root)) ? LexicalPath.Str(root) : LexicalPath.Parent(root));
    }

    // c.is_file(), keeping an entry whose name the runtime cannot decode and one whose stat raises (a file inside a directory
    // that cannot be searched is listed as unreadable).
    private static bool IsTarget(string candidate)
    {
        try
        {
            return EnginePath.IsFile(candidate) || EnginePath.IsUndecodableName(candidate);
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>
    /// <c>_iter_text</c>: the decoded sample, or null when the sample does not look like text. <c>handle.read(sample_size)</c>
    /// reads the whole file for a negative size and at most what the file holds otherwise, so the buffer grows with what is read
    /// instead of being allocated at the requested size.
    /// </summary>
    internal static string? ReadText(string path, long sampleSize)
    {
        using var stream = new FileStream(EnginePath.KernelPath(path), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var sample = new MemoryStream();
        var buffer = new byte[81920];
        var remaining = sampleSize < 0 ? long.MaxValue : sampleSize;
        while (remaining > 0)
        {
            var read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (read == 0)
            {
                break;
            }

            sample.Write(buffer, 0, read);
            remaining -= read;
        }

        var bytes = sample.GetBuffer().AsMemory(0, (int)sample.Length);
        return FormatRegistry.LooksText(bytes) ? FormatRegistry.DecodeText(bytes).Text : null;
    }

    /// <summary><c>_matches_keywords</c>: every keyword occurs in the lowered text.</summary>
    internal static bool MatchesKeywords(string text, IReadOnlyList<string> keywords)
    {
        var lowered = EngineText.Lower(text);
        return keywords.All(keyword => EngineText.Contains(lowered, keyword));
    }

    /// <summary>
    /// True when a pattern (<see cref="PathWildcard"/> syntax) matches the candidate's posix path relative to the root directory
    /// (<paramref name="relative"/>, when it has one), its last segment, or its full posix path. <c>*</c> crosses <c>/</c>, so
    /// <c>**/logs/*</c> excludes every file whose full path holds a <c>logs</c> directory.
    /// </summary>
    internal static bool ShouldExclude(string candidate, string? relative, IReadOnlyList<string> patterns)
    {
        var name = PathText.Name(candidate);
        var posix = PathText.ToPosix(candidate);
        return patterns.Any(pattern => (relative is not null && PathWildcard.IsMatch(relative, pattern))
            || PathWildcard.IsMatch(name, pattern)
            || PathWildcard.IsMatch(posix, pattern));
    }

    /// <summary><c>_deduplicate_preserving_order</c>: stripped, non-empty, first occurrence kept.</summary>
    private static List<string> Deduplicate(IEnumerable<string> values)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var ordered = new List<string>();
        foreach (var value in values)
        {
            var candidate = EngineText.Strip(value);
            if (candidate.Length > 0 && seen.Add(candidate))
            {
                ordered.Add(candidate);
            }
        }

        return ordered;
    }

    /// <summary>
    /// <c>_extract_hits</c>: per <c>str.splitlines()</c> line, the line-level keyword gate (any keyword), then every
    /// pattern's successive matches (<see cref="PatternRegex.Matches"/>); each match contributes its captured non-empty groups in
    /// group-number order and its non-empty whole match. A rule without patterns matches every line that passes the gate.
    /// </summary>
    internal static List<HuntFinding> ExtractHits(string text, HuntRule rule, string path, CancellationToken cancellationToken = default)
    {
        var hits = new List<HuntFinding>();
        var lines = TextLines.SplitLines(text);
        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            if (rule.Keywords.Count > 0)
            {
                var lineLower = EngineText.Lower(line);
                if (!rule.Keywords.Any(keyword => EngineText.Contains(lineLower, keyword)))
                {
                    continue;
                }
            }

            var (matched, values) = MatchLine(rule, line, cancellationToken);
            if (matched)
            {
                hits.Add(new HuntFinding(rule, path, index + 1, EngineText.Strip(line), Deduplicate(values)));
            }
        }

        return hits;
    }

    private static (bool Matched, List<string> Values) MatchLine(HuntRule rule, string line, CancellationToken cancellationToken)
    {
        var values = new List<string>();
        if (rule.Patterns.Count == 0)
        {
            values.Add(EngineText.Strip(line));
            return (true, values);
        }

        var matched = false;
        foreach (var pattern in rule.Patterns)
        {
            foreach (var match in PatternRegex.Matches(pattern, line, cancellationToken))
            {
                matched = true;
                for (var group = 1; group < match.Groups.Count; group++)
                {
                    if (match.Groups[group] is { Success: true, Length: > 0 } captured)
                    {
                        values.Add(EngineText.Strip(captured.Value));
                    }
                }

                if (match.Value.Length > 0)
                {
                    values.Add(EngineText.Strip(match.Value));
                }
            }
        }

        if (matched && values.Count == 0)
        {
            values.Add(EngineText.Strip(line));
        }

        return (matched, values);
    }

    /// <summary><c>_plan_transform_for_hit</c>.</summary>
    internal static PlanTransform? PlanTransformForHit(HuntFinding hit, string placeholderTemplate)
    {
        var tokenName = hit.Rule.TokenName;
        if (string.IsNullOrEmpty(tokenName))
        {
            return null;
        }

        string? value = null;
        if (hit.Matches.Count > 0)
        {
            value = hit.Matches.FirstOrDefault(candidate => candidate.AsSpan().IndexOfAny(".:/\\") >= 0) ?? hit.Matches[0];
        }
        else if (hit.Excerpt.Length > 0)
        {
            value = hit.Excerpt;
        }

        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        var placeholder = FormatPlaceholder(placeholderTemplate, tokenName);
        return new PlanTransform(tokenName, value, placeholder, hit.Rule.Name, hit.Path, hit.LineNumber, hit.Excerpt);
    }

    /// <summary><c>build_plan_transforms</c>: one transform per distinct (token, value, path, line).</summary>
    public static IReadOnlyList<PlanTransform> BuildPlanTransforms(IEnumerable<HuntFinding> hits, string placeholderTemplate = DefaultPlaceholderTemplate)
    {
        ArgumentNullException.ThrowIfNull(hits);
        var transforms = new List<PlanTransform>();
        var seen = new HashSet<(string, string, string, int)>();
        foreach (var hit in hits)
        {
            var transform = PlanTransformForHit(hit, placeholderTemplate);
            if (transform is not null && seen.Add((transform.TokenName, transform.Value, transform.Path, transform.LineNumber)))
            {
                transforms.Add(transform);
            }
        }

        return transforms;
    }

    /// <summary>
    /// The placeholder for <paramref name="tokenName"/> rendered from <paramref name="template"/> by
    /// <see cref="PlaceholderTemplate.Render"/>: a field other than <c>token_name</c> raises <see cref="EngineValueException"/>
    /// ("placeholder_template must include {token_name} placeholder"), malformed braces raise <see cref="FormatException"/>.
    /// </summary>
    internal static string FormatPlaceholder(string template, string tokenName)
        => PlaceholderTemplate.Render(template, tokenName);
}
