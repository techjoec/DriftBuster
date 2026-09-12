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

            var stripped = PythonText.Strip(tag);
            if (stripped.Length > 0)
            {
                cleaned.Add(stripped);
            }
        }

        return cleaned;
    }
}
