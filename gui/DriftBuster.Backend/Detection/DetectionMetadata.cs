using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using DriftBuster.Backend.Detection.Catalog;

namespace DriftBuster.Backend.Detection;

/// <summary>Metadata validation and catalog enrichment for detection matches.</summary>
public static partial class DetectionMetadata
{
    [GeneratedRegex("^[a-z0-9][a-z0-9_-]*$", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ValidIdentifier();

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
    /// Validates the match's format and variant against <paramref name="catalog"/> and adds the catalog keys to <c>match.Metadata</c>
    /// in place, returning it.
    /// With <paramref name="strict"/> the format and variant must be slugs known to the catalog; otherwise unknown
    /// formats pass through and the variant is only trimmed and lowered.
    /// </summary>
    public static JsonObject ValidateDetectionMetadata(DetectionMatch match, DetectionCatalog catalog, bool strict = true)
    {
        ArgumentNullException.ThrowIfNull(match);
        ArgumentNullException.ThrowIfNull(catalog);

        var metadata = match.Metadata;

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
        JsonObject metadata,
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

    private static void EnrichFromCatalog(JsonObject metadata, FormatClass formatEntry)
    {
        var severityValue = formatEntry.DefaultSeverity;
        var severityHintValue = formatEntry.SeverityHint;
        var remediationSources = new List<RemediationHint>(formatEntry.RemediationHints);

        if (metadata["catalog_variant"]?.GetValue<string>() is { Length: > 0 } variantKey)
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
                new JsonArray([.. formatEntry.References.Where(reference => !string.IsNullOrEmpty(reference)).Select(reference => (JsonNode?)reference)]));
        }
    }

    private static JsonArray RemediationPayload(IEnumerable<RemediationHint> sources)
    {
        var payload = new JsonArray();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var hint in sources)
        {
            if (!seenIds.Add(hint.Id))
            {
                continue;
            }

            var hintEntry = new JsonObject
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
}
