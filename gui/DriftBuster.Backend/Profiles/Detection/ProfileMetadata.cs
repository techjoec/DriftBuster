using System.Collections.ObjectModel;

using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Profiles.Detection;

/// <summary>Read-only profile metadata built from JSON values.</summary>
internal static class ProfileMetadata
{
    public static IReadOnlyDictionary<string, object?> Empty { get; } = Freeze(null);

    /// <summary>A read-only view over an order-preserving shallow copy.</summary>
    public static IReadOnlyDictionary<string, object?> Freeze(IEnumerable<KeyValuePair<string, object?>>? data)
    {
        var copy = new OrderedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in data ?? [])
        {
            copy[key] = value;
        }

        return new ReadOnlyDictionary<string, object?>(copy);
    }

    /// <summary>
    /// Metadata from a JSON value: falsy is empty, a dict is copied, a list is read as two-item pairs; anything else, or a non-string
    /// key, throws <see cref="InvalidDataException"/>.
    /// </summary>
    public static IReadOnlyDictionary<string, object?> FromValue(object? data)
    {
        if (!EngineBuiltins.IsTruthy(data))
        {
            return Empty;
        }

        return Freeze(EngineBuiltins.Dict(data, "metadata"));
    }
}
