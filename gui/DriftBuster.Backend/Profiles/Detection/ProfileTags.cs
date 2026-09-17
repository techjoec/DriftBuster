using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Profiles.Detection;

/// <summary>Tag helpers shared by profile stores and profile-aware scans.</summary>
public static class ProfileTags
{
    /// <summary><c>normalize_tags</c>: stripped, non-empty, deduplicated.</summary>
    public static IReadOnlySet<string> Normalize(IEnumerable<string?>? tags)
    {
        var cleaned = new HashSet<string>(StringComparer.Ordinal);
        if (tags is null)
        {
            return cleaned;
        }

        foreach (var tag in tags)
        {
            if (string.IsNullOrEmpty(tag))
            {
                continue;
            }

            var stripped = EngineText.Strip(tag);
            if (stripped.Length > 0)
            {
                cleaned.Add(stripped);
            }
        }

        return cleaned;
    }

    /// <summary>
    /// <c>normalize_tags</c> over a JSON value: None is empty; a str yields its code points, a list its items and a dict its keys
    /// (anything else raises <c>TypeError: '&lt;type&gt;' object is not iterable</c>); falsy items are skipped and any other item
    /// that is not a str raises <c>AttributeError</c> (<see cref="EngineAttributeException"/>) on <c>strip</c>.
    /// </summary>
    public static IReadOnlySet<string> NormalizeValue(object? tags)
    {
        if (tags is null)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        var items = new List<string?>();
        foreach (var item in EngineBuiltins.Iterate(tags))
        {
            if (!EngineBuiltins.IsTruthy(item))
            {
                continue;
            }

            items.Add(item as string ?? throw new EngineAttributeException($"expected a tag string, not '{EngineBuiltins.TypeName(item)}'"));
        }

        return Normalize(items);
    }
}
