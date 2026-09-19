namespace DriftBuster.Backend.Detection.Plugins;

internal static partial class BinaryPlist
{
    /// <summary>A dict payload's keys in ordinal order; none for any other payload.</summary>
    public static List<string> SortedKeys(object? payload)
        => payload is OrderedDictionary<string, object?> dict ? [.. dict.Keys.Order(StringComparer.Ordinal)] : [];
}
