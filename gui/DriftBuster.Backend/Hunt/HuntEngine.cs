using DriftBuster.Backend.Detection;
using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Hunt;

/// <summary><c>driftbuster.hunt</c>: search file trees for dynamic configuration values.</summary>
public static partial class HuntEngine
{
    /// <summary>Bytes read from each file.</summary>
    public const int DefaultSampleSize = 128 * 1024;

    /// <summary>The Python default <c>"{{{{ {token_name} }}}}"</c>, which formats to <c>{{ name }}</c>.</summary>
    public const string DefaultPlaceholderTemplate = "{{{{ {token_name} }}}}";

    /// <summary>Test seam for <c>Path.relative_to</c>: null stands for the <c>ValueError</c> Python raises.</summary>
    internal static Func<string, string, string?> RelativeTo { get; set; } = PythonPurePath.RelativeTo;

    /// <summary>
    /// <c>hunt_path(root, rules=..., glob=..., sample_size=..., exclude_patterns=...)</c>. A file root is scanned alone;
    /// a directory root is walked with <paramref name="glob"/> in <c>sorted(Path.glob)</c> order (symlinked directories
    /// not followed). Exclusion patterns are tried with <c>PurePath.match</c> against each file path and its path relative
    /// to the root directory.
    /// </summary>
    /// <remarks>
    /// Fix b: a file that cannot be opened, read or looked up (<c>is_file()</c> raising, as for a file inside a directory that
    /// cannot be searched) is skipped and listed in <see cref="HuntScanResult.UnreadableFiles"/>, as is an entry whose file name
    /// the runtime cannot decode (<see cref="PythonPath.IsUndecodableName"/>). Only regular files are
    /// read (<see cref="PythonPath.IsFile"/>): a FIFO, socket or device is skipped as Python skips it.
    /// Pattern searches have no time limit, as in Python; <paramref name="cancellationToken"/> is honoured while the tree is
    /// walked, between files and inside each pattern search. A root that does not exist yields no hits, as in Python; a directory
    /// root that cannot be listed raises its I/O error (plan decision 4; Python's glob swallows it). An empty or anchored
    /// <paramref name="glob"/> raises <see cref="ArgumentException"/> or <see cref="NotSupportedException"/>.
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
        var (targets, rootDirectory) = Targets(PythonPurePath.Str(root), glob, cancellationToken);
        var exclusions = excludePatterns ?? [];
        var hits = new List<HuntFinding>();
        var unreadable = new List<string>();
        foreach (var candidate in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (exclusions.Count > 0)
            {
                var relative = RelativeTo(candidate, rootDirectory);
                if (ShouldExclude(pattern => PythonPurePath.Match(candidate, pattern), relative, exclusions))
                {
                    continue;
                }
            }

            try
            {
                if (!PythonPath.IsFile(candidate))
                {
                    // An entry whose name the runtime cannot decode (Targets keeps it): Python would open it, the port cannot.
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
                        lowered ??= PythonText.Lower(text);
                        if (!rule.Keywords.All(keyword => PythonText.Contains(lowered, keyword)))
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
    // nothing, as in Python. Entries whose names the runtime cannot decode are kept, in walk order, so HuntPath reports them.
    private static (IReadOnlyList<string> Targets, string RootDirectory) Targets(string root, string glob, CancellationToken cancellationToken)
    {
        if (PythonPath.IsFile(root))
        {
            return ([root], PythonPurePath.Parent(root));
        }

        var targets = PythonPath.SortedGlob(root, glob, cancellationToken).Where(IsTarget).ToList();
        return (targets, Directory.Exists(root) ? PythonPurePath.Str(root) : PythonPurePath.Parent(root));
    }

    // c.is_file(), keeping an entry whose name the runtime cannot decode and one whose stat raises (a file inside a directory
    // that cannot be searched: Python's is_file() raises and aborts the hunt; fix b lists it as unreadable).
    private static bool IsTarget(string candidate)
    {
        try
        {
            return PythonPath.IsFile(candidate) || PythonPath.IsUndecodableName(candidate);
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
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
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
        var lowered = PythonText.Lower(text);
        return keywords.All(keyword => PythonText.Contains(lowered, keyword));
    }

    /// <summary><c>_should_exclude</c>: <paramref name="candidateMatches"/> stands for <c>candidate.match</c>.</summary>
    internal static bool ShouldExclude(Func<string, bool> candidateMatches, string? relative, IReadOnlyList<string> patterns)
    {
        foreach (var pattern in patterns)
        {
            if (candidateMatches(pattern))
            {
                return true;
            }

            if (relative is not null && PythonPurePath.Match(relative, pattern))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary><c>_should_exclude(candidate, relative=..., patterns=...)</c> for a real candidate path.</summary>
    internal static bool ShouldExclude(string candidate, string? relative, IReadOnlyList<string> patterns)
        => ShouldExclude(pattern => PythonPurePath.Match(candidate, pattern), relative, patterns);

    /// <summary><c>_deduplicate_preserving_order</c>: stripped, non-empty, first occurrence kept.</summary>
    private static List<string> Deduplicate(IEnumerable<string> values)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var ordered = new List<string>();
        foreach (var value in values)
        {
            var candidate = PythonText.Strip(value);
            if (candidate.Length > 0 && seen.Add(candidate))
            {
                ordered.Add(candidate);
            }
        }

        return ordered;
    }

    /// <summary>
    /// <c>_extract_hits</c>: per <c>str.splitlines()</c> line, the line-level keyword gate (any keyword), then every
    /// pattern's <c>finditer</c>; each match contributes its non-empty groups up to <c>lastindex</c> and its non-empty
    /// whole match. A rule without patterns matches every line that passes the gate.
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
                var lineLower = PythonText.Lower(line);
                if (!rule.Keywords.Any(keyword => PythonText.Contains(lineLower, keyword)))
                {
                    continue;
                }
            }

            var (matched, values) = MatchLine(rule, line, cancellationToken);
            if (matched)
            {
                hits.Add(new HuntFinding(rule, path, index + 1, PythonText.Strip(line), Deduplicate(values)));
            }
        }

        return hits;
    }

    private static (bool Matched, List<string> Values) MatchLine(HuntRule rule, string line, CancellationToken cancellationToken)
    {
        var values = new List<string>();
        if (rule.Patterns.Count == 0)
        {
            values.Add(PythonText.Strip(line));
            return (true, values);
        }

        var matched = false;
        foreach (var pattern in rule.Patterns)
        {
            foreach (var match in pattern.FindIter(line, cancellationToken))
            {
                matched = true;
                for (var group = 1; group <= (match.LastIndex ?? 0); group++)
                {
                    if (match.Group(group) is { Length: > 0 } value)
                    {
                        values.Add(PythonText.Strip(value));
                    }
                }

                if (match.Value.Length > 0)
                {
                    values.Add(PythonText.Strip(match.Value));
                }
            }
        }

        if (matched && values.Count == 0)
        {
            values.Add(PythonText.Strip(line));
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
    /// <c>placeholder_template.format(token_name=name)</c> (<see cref="PlaceholderFormatter"/>): a <c>KeyError</c> (a field
    /// other than <c>token_name</c>) becomes <see cref="ArgumentException"/> ("placeholder_template must include {token_name}
    /// placeholder"), as <c>_plan_transform_for_hit</c> re-raises it; every other <c>str.format</c> error propagates.
    /// </summary>
    internal static string FormatPlaceholder(string template, string tokenName)
    {
        try
        {
            return PlaceholderFormatter.Format(template, tokenName);
        }
        catch (KeyNotFoundException exc)
        {
            throw new PythonValueException("placeholder_template must include {token_name} placeholder", nameof(template), exc);
        }
    }
}
