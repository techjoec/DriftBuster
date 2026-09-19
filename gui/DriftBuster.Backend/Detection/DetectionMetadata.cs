using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;

using DriftBuster.Backend.Detection.Catalog;
using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Detection;

/// <summary>Metadata validation, catalog enrichment and JSON-safe conversion for detection matches.</summary>
public static partial class DetectionMetadata
{
    [GeneratedRegex("^[a-z0-9][a-z0-9_-]*$", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ValidIdentifier();

    internal static OrderedDictionary<string, object?> EnsureMapping(IEnumerable<KeyValuePair<string, object?>>? metadata)
    {
        var copy = new OrderedDictionary<string, object?>(StringComparer.Ordinal);
        if (metadata is null)
        {
            return copy;
        }

        foreach (var pair in metadata)
        {
            copy[pair.Key] = pair.Value;
        }

        return copy;
    }

    private static OrderedDictionary<string, object?> JsonSafeMapping(IEnumerable<KeyValuePair<string, object?>>? metadata)
    {
        var result = new OrderedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var pair in EnsureMapping(metadata))
        {
            result[pair.Key] = JsonSafe(pair.Value);
        }

        return result;
    }

    /// <summary>
    /// Converts a value to JSON-safe primitives: strings, integers, floats, bools and null pass through; byte arrays decode as UTF-8
    /// with replacement; dictionaries become ordered dictionaries with string keys (<see cref="EngineStr"/>); other enumerables
    /// become lists; anything else becomes <see cref="EngineStr"/>. Uses an explicit stack, so nesting depth is safe.
    /// </summary>
    public static object? JsonSafe(object? value)
    {
        if (!TryOpen(value, out var root))
        {
            return Scalar(value);
        }

        var open = new Stack<SafeContainer>();
        open.Push(root);
        while (open.Count > 0)
        {
            var container = open.Peek();
            if (!container.TryNext(out var key, out var item))
            {
                open.Pop();
                continue;
            }

            if (TryOpen(item, out var child))
            {
                container.Add(key, child.Result);
                open.Push(child);
            }
            else
            {
                container.Add(key, Scalar(item));
            }
        }

        return root.Result;
    }

    private static object? Scalar(object? value) => value switch
    {
        null or string or bool or byte or sbyte or short or ushort or int or uint or long or ulong or BigInteger or float or double or decimal => value,
        byte[] bytes => Encoding.UTF8.GetString(bytes),
        _ => EngineStr(value),
    };

    // Strings and byte arrays are scalars; any other dictionary or enumerable is a container.
    private static bool TryOpen(object? value, [NotNullWhen(true)] out SafeContainer? container)
    {
        container = value switch
        {
            null or string or byte[] => null,
            IDictionary dictionary => SafeContainer.ForDictionary(dictionary),
            IEnumerable enumerable when IsReadOnlyDictionary(enumerable) => SafeContainer.ForReadOnlyDictionary(enumerable),
            IEnumerable enumerable => SafeContainer.ForList(enumerable),
            _ => null,
        };
        return container is not null;
    }

    private static bool IsReadOnlyDictionary(IEnumerable enumerable)
        => enumerable.GetType().GetInterfaces()
            .Any(candidate => candidate.IsGenericType && candidate.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>));

    /// <summary>A container being converted: the source entries still to visit and the JSON-safe result being filled.</summary>
    private sealed class SafeContainer
    {
        private readonly IEnumerator _source;
        private readonly Func<object?, (string? Key, object? Item)> _split;
        private readonly OrderedDictionary<string, object?>? _dict;
        private readonly List<object?>? _list;

        private SafeContainer(IEnumerator source, Func<object?, (string? Key, object? Item)> split, bool isDictionary)
        {
            _source = source;
            _split = split;
            if (isDictionary)
            {
                _dict = new OrderedDictionary<string, object?>(StringComparer.Ordinal);
            }
            else
            {
                _list = [];
            }
        }

        public object Result => (object?)_dict ?? _list!;

        public static SafeContainer ForDictionary(IDictionary dictionary)
            => new(dictionary.GetEnumerator(), entry => (KeyText(((DictionaryEntry)entry!).Key), ((DictionaryEntry)entry!).Value), isDictionary: true);

        public static SafeContainer ForReadOnlyDictionary(IEnumerable pairs)
            => new(pairs.GetEnumerator(), SplitPair, isDictionary: true);

        public static SafeContainer ForList(IEnumerable items)
            => new(items.GetEnumerator(), item => (null, item), isDictionary: false);

        public bool TryNext(out string? key, out object? item)
        {
            (key, item) = (null, null);
            if (!_source.MoveNext())
            {
                return false;
            }

            (key, item) = _split(_source.Current);
            return true;
        }

        // A repeated key keeps its first slot with the last value.
        public void Add(string? key, object? converted)
        {
            if (_dict is not null)
            {
                _dict[key!] = converted;
            }
            else
            {
                _list!.Add(converted);
            }
        }

        private static (string? Key, object? Item) SplitPair(object? pair)
        {
            var itemType = pair!.GetType();
            return (KeyText(itemType.GetProperty("Key")!.GetValue(pair)), itemType.GetProperty("Value")!.GetValue(pair));
        }
    }

    private static string KeyText(object? key) => key is string text ? text : EngineStr(key);

    /// <summary>
    /// Text for the values plugins store: <c>null</c>, <c>true</c>/<c>false</c>, integers, floats as
    /// <c>repr</c>, a <see cref="DateTime"/> as <c>YYYY-MM-DD HH:MM:SS</c> with <c>.ffffff</c> only when there are
    /// microseconds, and any other object through its own <see cref="object.ToString"/>.
    /// </summary>
    internal static string EngineStr(object? value) => value switch
    {
        null => "null",
        string text => text,
        bool flag => flag ? "true" : "false",
        double number => EngineRepr.Float(number),
        float number => EngineRepr.Float(number),
        DateTime date => EngineDateTime(date),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
    };

    private static string EngineDateTime(DateTime date)
    {
        var seconds = date.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        var microseconds = date.Ticks % TimeSpan.TicksPerSecond / 10;
        return microseconds == 0 ? seconds : seconds + "." + microseconds.ToString("D6", CultureInfo.InvariantCulture);
    }

    private static string Slugify(string value) => value.Trim().ToLowerInvariant();

    internal static HashSet<string> CollectVariantIds(FormatClass format)
    {
        var variants = new HashSet<string>(StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(format.DefaultVariant))
        {
            variants.Add(Slugify(format.DefaultVariant));
        }

        foreach (var subtype in format.Subtypes)
        {
            if (!string.IsNullOrEmpty(subtype.Variant))
            {
                variants.Add(Slugify(subtype.Variant));
            }

            foreach (var alias in subtype.Aliases)
            {
                if (!string.IsNullOrEmpty(alias))
                {
                    variants.Add(Slugify(alias));
                }
            }
        }

        variants.Remove(string.Empty);
        return variants;
    }

    private static HashSet<string> FormatLookupKeys(FormatClass format)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal) { Slugify(format.Slug) };
        var nameKey = Slugify(format.Name);
        if (nameKey.Length > 0)
        {
            keys.Add(nameKey);
            try
            {
                keys.Add(NormaliseIdentifier(format.Name, "format_name"));
            }
            catch (MetadataValidationException)
            {
                // Names that are not valid slugs contribute only their lowered form.
            }
        }

        foreach (var alias in format.Aliases)
        {
            if (!string.IsNullOrEmpty(alias))
            {
                keys.Add(Slugify(alias));
            }
        }

        keys.Remove(string.Empty);
        return keys;
    }

    /// <summary>
    /// Maps every lookup key (slug, lowered name, slug-normalised name and aliases) to the canonical slug and the
    /// allowed variant ids; the first class to claim a key wins, and the fallback claims its keys last with no variants.
    /// </summary>
    internal static Dictionary<string, (string Canonical, HashSet<string> Variants)> BuildFormatLookup(DetectionCatalog catalog)
    {
        var lookup = new Dictionary<string, (string Canonical, HashSet<string> Variants)>(StringComparer.Ordinal);
        foreach (var format in catalog.Classes)
        {
            var canonical = Slugify(format.Slug);
            var variantIds = CollectVariantIds(format);
            foreach (var key in FormatLookupKeys(format))
            {
                lookup.TryAdd(key, (canonical, new HashSet<string>(variantIds, StringComparer.Ordinal)));
            }
        }

        var fallback = catalog.Fallback;
        var fallbackSlug = Slugify(fallback.Slug);
        var fallbackKeys = new HashSet<string>(StringComparer.Ordinal) { fallbackSlug, Slugify(fallback.Name) };
        foreach (var alias in fallback.Aliases)
        {
            if (!string.IsNullOrEmpty(alias))
            {
                fallbackKeys.Add(Slugify(alias));
            }
        }

        fallbackKeys.Remove(string.Empty);
        foreach (var key in fallbackKeys)
        {
            lookup.TryAdd(key, (fallbackSlug, new HashSet<string>(StringComparer.Ordinal)));
        }

        return lookup;
    }

    internal static FormatClass? FindFormatClass(DetectionCatalog catalog, string? slug)
    {
        if (string.IsNullOrEmpty(slug))
        {
            return null;
        }

        var slugKey = Slugify(slug);
        return catalog.Classes.FirstOrDefault(format => string.Equals(Slugify(format.Slug), slugKey, StringComparison.Ordinal));
    }

    internal static string NormaliseIdentifier(string raw, string field)
    {
        var identifier = Slugify(raw);
        if (identifier.Length == 0)
        {
            throw new MetadataValidationException($"{field} cannot be empty.");
        }

        if (!ValidIdentifier().IsMatch(identifier))
        {
            throw new MetadataValidationException(
                $"{field} must be a lowercase slug containing letters, numbers, hyphen, or underscore.");
        }

        return identifier;
    }

    /// <summary>
    /// Validates and enriches <c>match.Metadata</c> against <paramref name="catalog"/>, returning a sanitised copy.
    /// With <paramref name="strict"/> the format and variant must be slugs known to the catalog; otherwise unknown
    /// formats pass through and the variant is only trimmed and lowered.
    /// </summary>
    public static OrderedDictionary<string, object?> ValidateDetectionMetadata(DetectionMatch match, DetectionCatalog catalog, bool strict = true)
    {
        ArgumentNullException.ThrowIfNull(match);
        ArgumentNullException.ThrowIfNull(catalog);

        var metadata = JsonSafeMapping(match.Metadata);

        var formatName = match.FormatName;
        if (formatName is null)
        {
            throw new MetadataValidationException("DetectionMatch.format_name must be a string.");
        }

        var formatId = NormaliseIdentifier(formatName, "format_name");

        var lookup = BuildFormatLookup(catalog);
        string canonicalFormat;
        HashSet<string> allowedVariants;
        if (lookup.TryGetValue(formatId, out var entry))
        {
            (canonicalFormat, allowedVariants) = entry;
        }
        else if (strict)
        {
            throw new MetadataValidationException($"Unknown catalog format: {formatId}");
        }
        else
        {
            canonicalFormat = formatId;
            allowedVariants = new HashSet<string>(StringComparer.Ordinal);
        }

        metadata["catalog_version"] = catalog.Version;
        metadata["catalog_format"] = canonicalFormat;

        ApplyVariant(match.Variant, metadata, canonicalFormat, allowedVariants, strict);

        var formatEntry = FindFormatClass(catalog, canonicalFormat);
        if (formatEntry is not null)
        {
            EnrichFromCatalog(metadata, formatEntry);
        }

        return metadata;
    }

    private static void ApplyVariant(
        string? variant,
        OrderedDictionary<string, object?> metadata,
        string canonicalFormat,
        HashSet<string> allowedVariants,
        bool strict)
    {
        if (variant is null)
        {
            metadata.Remove("catalog_variant");
            return;
        }

        var variantId = strict ? NormaliseIdentifier(variant, "variant") : Slugify(variant);
        if (variantId.Length == 0)
        {
            return;
        }

        if (strict && allowedVariants.Count > 0 && !allowedVariants.Contains(variantId))
        {
            throw new MetadataValidationException(
                $"Unknown catalog variant '{variantId}' for format '{canonicalFormat}'.");
        }

        metadata["catalog_variant"] = variantId;
    }

    private static FormatSubtype? FindSubtype(FormatClass formatEntry, string variantKey)
    {
        foreach (var subtype in formatEntry.Subtypes)
        {
            var subtypeVariant = string.IsNullOrEmpty(subtype.Variant) ? null : Slugify(subtype.Variant);
            if (string.Equals(subtypeVariant, variantKey, StringComparison.Ordinal)
                || subtype.Aliases.Any(alias => string.Equals(Slugify(alias), variantKey, StringComparison.Ordinal)))
            {
                return subtype;
            }
        }

        return null;
    }

    private static void EnrichFromCatalog(OrderedDictionary<string, object?> metadata, FormatClass formatEntry)
    {
        var severityValue = formatEntry.DefaultSeverity;
        var severityHintValue = formatEntry.SeverityHint;
        var remediationSources = new List<RemediationHint>(formatEntry.RemediationHints);

        if (metadata.TryGetValue("catalog_variant", out var variantObject) && variantObject is string variantKey && variantKey.Length > 0)
        {
            var subtype = FindSubtype(formatEntry, variantKey);
            if (subtype is not null)
            {
                if (!string.IsNullOrEmpty(subtype.Severity))
                {
                    severityValue = subtype.Severity;
                }

                if (!string.IsNullOrEmpty(subtype.SeverityHint))
                {
                    severityHintValue = subtype.SeverityHint;
                }

                remediationSources.AddRange(subtype.RemediationHints);
            }
        }

        if (!string.IsNullOrEmpty(severityValue))
        {
            metadata.TryAdd("catalog_severity", severityValue);
        }

        if (!string.IsNullOrEmpty(severityHintValue))
        {
            metadata.TryAdd("catalog_severity_hint", severityHintValue);
        }

        if (remediationSources.Count > 0)
        {
            metadata.TryAdd("catalog_remediations", RemediationPayload(remediationSources));
        }

        if (formatEntry.References.Count > 0)
        {
            metadata.TryAdd(
                "catalog_references",
                formatEntry.References.Where(reference => !string.IsNullOrEmpty(reference)).Cast<object?>().ToList());
        }
    }

    private static List<object?> RemediationPayload(IEnumerable<RemediationHint> sources)
    {
        var payload = new List<object?>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var hint in sources)
        {
            if (!seenIds.Add(hint.Id))
            {
                continue;
            }

            var hintEntry = new OrderedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = hint.Id,
                ["category"] = hint.Category,
                ["summary"] = hint.Summary,
            };
            if (!string.IsNullOrEmpty(hint.Documentation))
            {
                hintEntry["documentation"] = hint.Documentation;
            }

            payload.Add(hintEntry);
        }

        return payload;
    }

    /// <summary>A JSON-ready mapping describing <paramref name="match"/> and its normalised metadata.</summary>
    public static OrderedDictionary<string, object?> SummariseMetadata(DetectionMatch match)
    {
        ArgumentNullException.ThrowIfNull(match);

        return new OrderedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["plugin"] = match.PluginName,
            ["format"] = match.FormatName,
            ["variant"] = match.Variant,
            ["confidence"] = match.Confidence,
            ["reasons"] = new List<string>(match.Reasons),
            ["metadata"] = JsonSafeMapping(match.Metadata),
        };
    }
}
