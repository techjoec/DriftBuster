using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Profiles.Detection;

/// <summary>Tag helpers shared by profile stores and profile-aware scans.</summary>
public static class ProfileTags
{
    /// <summary>Trimmed, non-empty, de-duplicated tags.</summary>
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
    /// <see cref="Normalize"/> over a JSON value: null is empty; a string yields its characters, a list its items, a dict its keys
    /// (anything else throws <see cref="InvalidDataException"/>); falsy items are skipped and other non-string items throw.
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

            items.Add(item as string ?? throw new InvalidDataException($"expected a tag string, not '{EngineBuiltins.TypeName(item)}'"));
        }

        return Normalize(items);
    }
}
