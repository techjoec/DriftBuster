using System.Collections;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Diff;

/// <summary>Replaces known tokens with a placeholder and counts every replacement per token.</summary>
/// <remarks>
/// Tokens are de-duplicated (first kept), empties dropped, then ordered longest first by code points, ties in given order.
/// Matches are non-overlapping, left to right, on code-point boundaries, so a token never matches half a surrogate pair.
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

    public IReadOnlyList<string> Tokens { get; }

    public string Placeholder { get; }

    /// <summary>Unique, non-empty tokens, longest first.</summary>
    public IReadOnlyList<string> OrderedTokens { get; }

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

    /// <summary>Redacts every string value inside <paramref name="node"/> in place (property names are left alone) and returns it.</summary>
    public JsonNode? ApplyTo(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject item:
                foreach (var name in item.Select(pair => pair.Key).ToList())
                {
                    Replace(item[name], redacted => item[name] = redacted);
                }

                return item;
            case JsonArray items:
                for (var index = 0; index < items.Count; index++)
                {
                    var position = index;
                    Replace(items[position], redacted => items[position] = redacted);
                }

                return items;
            case JsonValue value when value.GetValueKind() == JsonValueKind.String:
                return JsonValue.Create(Apply(value.GetValue<string>()));
            default:
                return node;
        }
    }

    // A string child is swapped for its redacted copy; a container is redacted where it stands.
    private void Replace(JsonNode? child, Action<JsonNode?> swap)
    {
        if (child is JsonValue value && value.GetValueKind() == JsonValueKind.String)
        {
            swap(JsonValue.Create(Apply(value.GetValue<string>())));
        }
        else
        {
            ApplyTo(child);
        }
    }

    /// <summary>A copy of the per-token counts in first-hit order.</summary>
    public OrderedDictionary<string, int> Stats() => new(_hits, StringComparer.Ordinal);

    public void Reset() => _hits.Clear();

    /// <summary>
    /// The explicit redactor, or a new filter over <paramref name="maskTokens"/>, or null when neither is given. Both at once (with a
    /// token) throws <see cref="ArgumentException"/>.
    /// </summary>
    public static RedactionFilter? Resolve(RedactionFilter? redactor = null, IReadOnlyList<string>? maskTokens = null, string placeholder = DefaultPlaceholder)
    {
        var hasTokens = maskTokens is { Count: > 0 };
        if (redactor is not null && hasTokens)
        {
            throw new ArgumentException("Provide either an explicit redactor or mask_tokens, not both.", nameof(maskTokens));
        }

        if (redactor is not null)
        {
            return redactor;
        }

        return hasTokens ? new RedactionFilter(maskTokens, placeholder) : null;
    }

    /// <summary>
    /// Redacts strings inside any value: dictionaries become ordered dictionaries with string keys, object arrays stay arrays, sets
    /// stay sets, byte arrays are unchanged, lists and other enumerables become lists, everything else is unchanged.
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
