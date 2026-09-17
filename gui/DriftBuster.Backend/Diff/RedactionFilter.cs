using System.Collections;
using System.Text;

using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Diff;

/// <summary>
/// Replaces known tokens with a placeholder and counts every
/// replacement per token.
/// </summary>
/// <remarks>
/// Tokens are deduplicated keeping first occurrences, empty tokens dropped, then ordered longest first by code-point
/// length with ties kept in their given order (Python's stable <c>sort(key=len, reverse=True)</c>). Matching is
/// <c>str.count</c> / <c>str.replace</c>: non-overlapping, left to right, on whole code points, so a token never
/// matches half of a surrogate pair.
/// </remarks>
public class RedactionFilter
{
    public const string DefaultPlaceholder = "[REDACTED]";

    private readonly OrderedDictionary<string, int> _hits = new(StringComparer.Ordinal);

    public RedactionFilter(IEnumerable<string>? tokens = null, string placeholder = DefaultPlaceholder)
    {
        ArgumentNullException.ThrowIfNull(placeholder);
        Tokens = (tokens ?? []).ToArray();
        Placeholder = placeholder;
        OrderedTokens = Tokens
            .Distinct(StringComparer.Ordinal)
            .Where(token => token.Length > 0)
            .OrderByDescending(CodePointLength)
            .ToArray();
    }

    /// <summary>The tokens as given.</summary>
    public IReadOnlyList<string> Tokens { get; }

    public string Placeholder { get; }

    /// <summary><c>_ordered_tokens</c>: unique, non-empty, longest first.</summary>
    public IReadOnlyList<string> OrderedTokens { get; }

    /// <summary><c>has_hits</c>.</summary>
    public bool HasHits => _hits.Count > 0;

    /// <summary>Returns <paramref name="text"/> with each configured token replaced by the placeholder.</summary>
    public virtual string Apply(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (OrderedTokens.Count == 0 || text.Length == 0)
        {
            return text;
        }

        var result = text;
        foreach (var token in OrderedTokens)
        {
            var occurrences = FindOccurrences(result, token);
            if (occurrences.Count == 0)
            {
                continue;
            }

            _hits[token] = (_hits.TryGetValue(token, out var existing) ? existing : 0) + occurrences.Count;
            result = ReplaceAt(result, token.Length, occurrences, Placeholder);
        }

        return result;
    }

    /// <summary><c>stats()</c>: a copy of the per-token counts in first-hit order.</summary>
    public OrderedDictionary<string, int> Stats() => new(_hits, StringComparer.Ordinal);

    /// <summary><c>reset()</c>.</summary>
    public void Reset() => _hits.Clear();

    /// <summary>
    /// <c>resolve_redactor</c>: the explicit redactor, or a new filter over <paramref name="maskTokens"/>, or null when
    /// neither is given. Supplying both (with at least one mask token) raises <see cref="ArgumentException"/>, Python's
    /// <c>ValueError</c>.
    /// </summary>
    public static RedactionFilter? Resolve(RedactionFilter? redactor = null, IReadOnlyList<string>? maskTokens = null, string placeholder = DefaultPlaceholder)
    {
        var hasTokens = maskTokens is { Count: > 0 };
        if (redactor is not null && hasTokens)
        {
            throw new EngineValueException("Provide either an explicit redactor or mask_tokens, not both.", nameof(maskTokens));
        }

        if (redactor is not null)
        {
            return redactor;
        }

        return hasTokens ? new RedactionFilter(maskTokens, placeholder) : null;
    }

    /// <summary>
    /// <c>redact_data</c> over Python's runtime categories rather than the static .NET type: strings are redacted; a dictionary
    /// becomes an ordered dictionary keyed by <c>str(key)</c>; an array of any element type (the tuple) stays an
    /// <see cref="object"/> array; a set of any element type (<see cref="ISet{T}"/> or <see cref="IReadOnlySet{T}"/>) stays a
    /// set; a byte array (<c>bytes</c>) is returned unchanged; a list and any other enumerable become a list; everything else
    /// is returned unchanged.
    /// </summary>
    public static object? RedactData(object? data, RedactionFilter redactor)
    {
        ArgumentNullException.ThrowIfNull(redactor);
        switch (data)
        {
            case string text:
                return redactor.Apply(text);
            case IDictionary mapping:
                var redacted = new OrderedDictionary<string, object?>(StringComparer.Ordinal);
                foreach (DictionaryEntry entry in mapping)
                {
                    redacted[EngineRepr.Str(entry.Key)] = RedactData(entry.Value, redactor);
                }

                return redacted;
            case byte[]:
                return data;
            case Array tuple:
                return tuple.Cast<object?>().Select(item => RedactData(item, redactor)).ToArray();
            case IEnumerable set when IsSet(set.GetType()):
                return new HashSet<object?>(set.Cast<object?>().Select(item => RedactData(item, redactor)));
            case IEnumerable items:
                return items.Cast<object?>().Select(item => RedactData(item, redactor)).ToList();
            default:
                return data;
        }
    }

    private static bool IsSet(Type type) => type.GetInterfaces().Any(contract => contract.IsGenericType
        && (contract.GetGenericTypeDefinition() == typeof(ISet<>) || contract.GetGenericTypeDefinition() == typeof(IReadOnlySet<>)));

    private static int CodePointLength(string token)
    {
        var length = token.Length;
        for (var index = 0; index + 1 < token.Length; index++)
        {
            if (char.IsSurrogatePair(token[index], token[index + 1]))
            {
                length--;
                index++;
            }
        }

        return length;
    }

    // Non-overlapping left-to-right matches that start and end on code-point boundaries.
    private static List<int> FindOccurrences(string text, string token)
    {
        var found = new List<int>();
        var start = 0;
        while (start <= text.Length - token.Length)
        {
            var index = text.IndexOf(token, start, StringComparison.Ordinal);
            if (index < 0)
            {
                break;
            }

            var end = index + token.Length;
            var splitsStart = index > 0 && char.IsHighSurrogate(text[index - 1]) && char.IsLowSurrogate(text[index]);
            var splitsEnd = end < text.Length && char.IsHighSurrogate(text[end - 1]) && char.IsLowSurrogate(text[end]);
            if (splitsStart || splitsEnd)
            {
                start = index + 1;
                continue;
            }

            found.Add(index);
            start = end;
        }

        return found;
    }

    private static string ReplaceAt(string text, int tokenLength, List<int> occurrences, string placeholder)
    {
        var builder = new StringBuilder(text.Length);
        var previous = 0;
        foreach (var index in occurrences)
        {
            builder.Append(text, previous, index - previous).Append(placeholder);
            previous = index + tokenLength;
        }

        return builder.Append(text, previous, text.Length - previous).ToString();
    }
}
