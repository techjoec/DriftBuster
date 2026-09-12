using System.Collections;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

using DriftBuster.Backend.Detection.Catalog;

namespace DriftBuster.Backend.Detection;

/// <summary>Metadata validation, catalog enrichment and JSON-safe conversion for detection matches.</summary>
public static partial class DetectionMetadata
{
    [GeneratedRegex("^[a-z0-9][a-z0-9_-]*$", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ValidIdentifier();

    /// <summary>Copies <paramref name="metadata"/> (or an empty dictionary when null) preserving insertion order.</summary>
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
    /// Converts a value into JSON-serialisable primitives: strings, numbers, booleans and null pass through, byte
    /// arrays decode as UTF-8 with replacement, dictionaries become ordered string-keyed dictionaries, other
    /// enumerables become lists, and everything else becomes its string form.
    /// </summary>
    public static object? JsonSafe(object? value)
    {
        switch (value)
        {
            case null:
            case string:
            case bool:
            case byte:
            case sbyte:
            case short:
            case ushort:
            case int:
            case uint:
            case long:
            case ulong:
            case float:
            case double:
            case decimal:
                return value;
            case byte[] bytes:
                return Encoding.UTF8.GetString(bytes);
            case IDictionary dictionary:
                {
                    var result = new OrderedDictionary<string, object?>(StringComparer.Ordinal);
                    foreach (DictionaryEntry entry in dictionary)
                    {
                        result[KeyText(entry.Key)] = JsonSafe(entry.Value);
                    }

                    return result;
                }
            case IEnumerable enumerable when TryReadOnlyDictionary(enumerable, out var readOnly):
                return readOnly;
            case IEnumerable enumerable:
                {
                    var result = new List<object?>();
                    foreach (var item in enumerable)
                    {
                        result.Add(JsonSafe(item));
                    }

                    return result;
                }
            default:
                return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        }
    }

    private static bool TryReadOnlyDictionary(IEnumerable enumerable, out OrderedDictionary<string, object?>? result)
    {
        result = null;
        var type = enumerable.GetType();
        var isReadOnlyDictionary = type.GetInterfaces()
            .Any(candidate => candidate.IsGenericType && candidate.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>));
        if (!isReadOnlyDictionary)
        {
            return false;
        }

        result = new OrderedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var item in enumerable)
        {
            var itemType = item!.GetType();
            var key = itemType.GetProperty("Key")!.GetValue(item);
            var entryValue = itemType.GetProperty("Value")!.GetValue(item);
            result[KeyText(key)] = JsonSafe(entryValue);
        }

        return true;
    }

    private static string KeyText(object? key) => Convert.ToString(key, CultureInfo.InvariantCulture) ?? string.Empty;

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
            catch (MetadataValidationError)
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
            throw new MetadataValidationError($"{field} cannot be empty.");
        }

        if (!ValidIdentifier().IsMatch(identifier))
        {
            throw new MetadataValidationError(
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
            throw new MetadataValidationError("DetectionMatch.format_name must be a string.");
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
            throw new MetadataValidationError($"Unknown catalog format: {formatId}");
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
            throw new MetadataValidationError(
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
