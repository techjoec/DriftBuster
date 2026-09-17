using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace DriftBuster.Backend.Infrastructure;

/// <summary>
/// Builds the <see cref="Regex"/> objects for hunt rules, secret rules, secret ignore patterns and registry search patterns.
/// Patterns use .NET regular expression syntax and are always culture-invariant. Each pattern is tried with
/// <see cref="RegexOptions.NonBacktracking"/> first, which matches in time linear in the input; a pattern that engine cannot run
/// (backreferences, lookarounds, atomic groups, conditionals) is built with the default engine, the same options and
/// <see cref="AttemptTimeout"/>, so a pattern that backtracks catastrophically cannot run away with the scan.
/// Built instances are cached by pattern text and options.
/// </summary>
public static class PatternRegex
{
    /// <summary>
    /// The time limit on one match attempt made by the backtracking engine. Patterns that run on
    /// <see cref="RegexOptions.NonBacktracking"/> are linear in the input and need no limit; the fallback engine does not
    /// bound its own work, so an attempt that passes this limit is abandoned and the search moves on, and a caller's
    /// cancellation is therefore observed within this limit.
    /// </summary>
    public static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(2);

    // Bounds the cache when callers feed many distinct user patterns; the cache is simply emptied once it is reached.
    private const int CacheLimit = 512;

    private static readonly ConcurrentDictionary<(string Pattern, RegexOptions Options), Regex> Cache = new();

    /// <summary>Compiles <paramref name="pattern"/>; a pattern that does not parse throws <see cref="RegexParseException"/>.</summary>
    public static Regex Create(string pattern, RegexOptions options = RegexOptions.None)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        var key = (Pattern: pattern, Options: options | RegexOptions.CultureInvariant);
        if (Cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var built = Build(key.Pattern, key.Options);
        if (Cache.Count >= CacheLimit)
        {
            Cache.Clear();
        }

        return Cache.GetOrAdd(key, built);
    }

    private static Regex Build(string pattern, RegexOptions options)
    {
        try
        {
            return new Regex(pattern, options | RegexOptions.NonBacktracking, Regex.InfiniteMatchTimeout);
        }
        catch (NotSupportedException)
        {
            return new Regex(pattern, options, AttemptTimeout);
        }
    }

    /// <summary>
    /// Successive non-overlapping matches of <paramref name="regex"/> in <paramref name="text"/>. The token is checked before the
    /// first match attempt and before each following one; an attempt that passes <see cref="AttemptTimeout"/> ends the
    /// enumeration, so a pattern the backtracking engine cannot finish yields what it found before the limit.
    /// </summary>
    public static IEnumerable<Match> Matches(Regex regex, string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(regex);
        ArgumentNullException.ThrowIfNull(text);
        return Enumerate(regex, text, cancellationToken);
    }

    private static IEnumerable<Match> Enumerate(Regex regex, string text, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var match = Attempt(() => regex.Match(text), cancellationToken);
        while (match is not null && match.Success)
        {
            yield return match;
            cancellationToken.ThrowIfCancellationRequested();
            var current = match;
            match = Attempt(current.NextMatch, cancellationToken);
        }
    }

    /// <summary>
    /// The first match anywhere in <paramref name="text"/>, or null; the token is checked before the attempt, and an attempt
    /// that passes <see cref="AttemptTimeout"/> reports no match.
    /// </summary>
    public static Match? Search(Regex regex, string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(regex);
        ArgumentNullException.ThrowIfNull(text);
        cancellationToken.ThrowIfCancellationRequested();
        var match = Attempt(() => regex.Match(text), cancellationToken);
        return match is not null && match.Success ? match : null;
    }

    /// <summary>
    /// True when <paramref name="regex"/> matches anywhere in <paramref name="text"/>; an attempt that passes
    /// <see cref="AttemptTimeout"/> reports no match.
    /// </summary>
    public static bool IsMatch(Regex regex, string text, CancellationToken cancellationToken = default)
        => Search(regex, text, cancellationToken) is not null;

    // One match attempt: null when the engine passed its time limit. A cancelled token is reported as cancellation rather than
    // as an abandoned attempt, since that is what the caller asked for.
    private static Match? Attempt(Func<Match> attempt, CancellationToken cancellationToken)
    {
        try
        {
            return attempt();
        }
        catch (RegexMatchTimeoutException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }
    }
}
