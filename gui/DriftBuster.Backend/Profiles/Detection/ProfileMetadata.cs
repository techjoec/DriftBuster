using System.Collections.ObjectModel;

using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Profiles.Detection;

/// <summary><c>_freeze_mapping</c> and the <c>dict(data or {})</c> it applies to JSON values.</summary>
internal static class ProfileMetadata
{
    public static IReadOnlyDictionary<string, object?> Empty { get; } = Freeze(null);

    /// <summary><c>MappingProxyType(dict(data or {}))</c>: a read-only view over a shallow, order-preserving copy.</summary>
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
    /// <c>dict(data or {})</c> over a JSON value: a falsy value is empty, a dict is copied, a list is read as key/value pairs
    /// (each item an iterable of exactly two elements) with Python's errors, and any other value raises <c>TypeError</c>.
    /// </summary>
    /// <remarks>A pair whose key is not a str raises <see cref="PythonTypeException"/> (<see cref="PythonBuiltins.Dict"/>): the typed metadata holds str keys only.</remarks>
    public static IReadOnlyDictionary<string, object?> FromValue(object? data)
    {
        if (!PythonBuiltins.IsTruthy(data))
        {
            return Empty;
        }

        return Freeze(PythonBuiltins.Dict(data, "metadata"));
    }
}
