using System.Collections.Concurrent;

namespace DriftBuster.Backend.Infrastructure.EngineRe;

/// <summary>
/// A compiled Python <c>re</c> pattern (<c>re.Pattern</c>) for str subjects: CPython 3.13's parser, compiler and <c>_sre</c>
/// matching engine.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Compile"/> raises <see cref="EngineReException"/> wherever <c>re.compile</c> raises <c>re.error</c>,
/// <see cref="OverflowException"/> for a repeat count of 2**32 - 1 or more (Python's <c>OverflowError</c>) and
/// <see cref="ArgumentException"/> for a LOCALE flag or ASCII with UNICODE (Python's <c>ValueError</c>).
/// </para>
/// <para>
/// Matching runs <see cref="ReMatcher"/> over the subject's code points, so spans, groups and <c>lastindex</c> are Python's.
/// Offsets on <see cref="EngineMatch"/> are UTF-16 indices into the subject string, ready for slicing; a code point offset
/// never falls inside a surrogate pair, so the two always denote the same text. Like Python, a search has no time limit: its
/// result never depends on host speed. A search polls its <see cref="CancellationToken"/> while it runs and throws
/// <see cref="OperationCanceledException"/> once the token is cancelled.
/// </para>
/// </remarks>
public sealed class EnginePattern
{
    private const int CacheLimit = 512;

    private static readonly ConcurrentDictionary<(string Pattern, EngineReFlags Flags), EnginePattern> Cache = new();

    private readonly ReProgram _program;

    private EnginePattern(string pattern, ReSubPattern parsed, EngineReFlags flags)
    {
        Pattern = pattern;
        Flags = parsed.State.Flags;
        Groups = parsed.State.Groups - 1;
        GroupIndex = new Dictionary<string, int>(parsed.State.GroupDict, StringComparer.Ordinal);
        _program = ReCompiler.Compile(parsed, flags);
    }

    /// <summary><c>pattern.pattern</c>.</summary>
    public string Pattern { get; }

    /// <summary><c>pattern.flags</c>: the given and inline flags, with UNICODE implied unless ASCII is set.</summary>
    public EngineReFlags Flags { get; }

    /// <summary><c>pattern.groups</c>.</summary>
    public int Groups { get; }

    /// <summary><c>pattern.groupindex</c>.</summary>
    public IReadOnlyDictionary<string, int> GroupIndex { get; }

    /// <summary><c>re.compile(pattern, flags)</c>, with <c>re</c>'s compiled-pattern cache.</summary>
    public static EnginePattern Compile(string pattern, EngineReFlags flags = EngineReFlags.None)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        if (Cache.TryGetValue((pattern, flags), out var cached))
        {
            return cached;
        }

        var compiled = new EnginePattern(pattern, ReParser.Parse(pattern, flags), flags);
        if (Cache.Count >= CacheLimit)
        {
            Cache.Clear();
        }

        Cache[(pattern, flags)] = compiled;
        return compiled;
    }

    /// <summary><c>pattern.match(text)</c>: a match that starts at the beginning of <paramref name="text"/>.</summary>
    public EngineMatch? Match(string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        var subject = new Subject(text);
        var matcher = NewMatcher(subject, cancellationToken);
        return matcher.MatchHere() ? subject.MatchFrom(this, matcher) : null;
    }

    /// <summary><c>pattern.search(text)</c>.</summary>
    public EngineMatch? Search(string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        var subject = new Subject(text);
        var matcher = NewMatcher(subject, cancellationToken);
        return matcher.Search() ? subject.MatchFrom(this, matcher) : null;
    }

    /// <summary>
    /// <c>pattern.finditer(text)</c>: the scanner searches again from the end of each match, refusing an empty match at the
    /// position where an empty match just ended (so <c>a??</c> over "a" yields "", "a", "").
    /// </summary>
    public IEnumerable<EngineMatch> FindIter(string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        return FindIterCore(text, cancellationToken);
    }

    private IEnumerable<EngineMatch> FindIterCore(string text, CancellationToken cancellationToken)
    {
        var subject = new Subject(text);
        var matcher = NewMatcher(subject, cancellationToken);
        var start = 0;
        var mustAdvance = false;
        while (matcher.ScannerSearch(start, mustAdvance))
        {
            yield return subject.MatchFrom(this, matcher);
            mustAdvance = matcher.Ptr == matcher.Start;
            start = matcher.Ptr;
        }
    }

    /// <summary>The compiled program, for tests that drive <see cref="ReMatcher"/> directly.</summary>
    internal ReProgram Program => _program;

    private ReMatcher NewMatcher(Subject subject, CancellationToken cancellationToken)
    {
        var matcher = new ReMatcher(_program, cancellationToken);
        matcher.Reset(subject.CodePoints, 0);
        return matcher;
    }

    // The subject as code points, with the UTF-16 offset of every code point offset when the two differ.
    private sealed class Subject
    {
        private readonly int[]? _utf16Offsets;

        public Subject(string text)
        {
            Text = text;
            CodePoints = ReTokenizer.CodePoints(text);
            if (CodePoints.Length != text.Length)
            {
                _utf16Offsets = new int[CodePoints.Length + 1];
                var offset = 0;
                for (var index = 0; index < CodePoints.Length; index++)
                {
                    _utf16Offsets[index] = offset;
                    offset += CodePoints[index] > 0xFFFF ? 2 : 1;
                }

                _utf16Offsets[CodePoints.Length] = offset;
            }
        }

        public string Text { get; }

        public int[] CodePoints { get; }

        public EngineMatch MatchFrom(EnginePattern pattern, ReMatcher matcher)
        {
            var spans = new int[(pattern.Groups + 1) * 2];
            spans[0] = Utf16(matcher.Start);
            spans[1] = Utf16(matcher.Ptr);
            for (var group = 1; group <= pattern.Groups; group++)
            {
                var (start, end) = matcher.GroupSpan(group);
                spans[group * 2] = start < 0 ? -1 : Utf16(start);
                spans[(group * 2) + 1] = end < 0 ? -1 : Utf16(end);
            }

            return new EngineMatch(pattern, Text, spans, matcher.LastIndex < 0 ? null : matcher.LastIndex);
        }

        private int Utf16(int codePointOffset) => _utf16Offsets is null ? codePointOffset : _utf16Offsets[codePointOffset];
    }
}
