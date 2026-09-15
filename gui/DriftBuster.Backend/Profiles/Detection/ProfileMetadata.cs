using System.Collections;
using System.Collections.ObjectModel;
using System.Globalization;

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
    /// <remarks>A pair whose key is not a str raises <see cref="PythonTypeException"/>: the typed metadata holds str keys only.</remarks>
    public static IReadOnlyDictionary<string, object?> FromValue(object? data)
    {
        if (!PythonBuiltins.IsTruthy(data))
        {
            return Empty;
        }

        if (data is IReadOnlyDictionary<string, object?> mapping)
        {
            return Freeze(mapping);
        }

        if (data is not (IList or string))
        {
            throw new PythonTypeException($"'{PythonBuiltins.TypeName(data)}' object is not iterable", nameof(data));
        }

        var pairs = new List<KeyValuePair<string, object?>>();
        var index = 0;
        foreach (var item in PythonBuiltins.Iterate(data))
        {
            pairs.Add(Pair(item, index));
            index++;
        }

        return Freeze(pairs);
    }

    private static KeyValuePair<string, object?> Pair(object? item, int index)
    {
        if (item is not (string or IList or IReadOnlyDictionary<string, object?>))
        {
            throw new PythonTypeException(
                string.Create(CultureInfo.InvariantCulture, $"cannot convert dictionary update sequence element #{index} to a sequence"),
                nameof(item));
        }

        var elements = PythonBuiltins.Iterate(item).ToList();
        if (elements.Count != 2)
        {
            throw new PythonValueException(
                string.Create(CultureInfo.InvariantCulture, $"dictionary update sequence element #{index} has length {elements.Count}; 2 is required"),
                nameof(item));
        }

        if (elements[0] is { } candidate)
        {
            _ = PythonValues.HashKeys.GetHashCode(candidate);
        }

        return elements[0] is string key
            ? new KeyValuePair<string, object?>(key, elements[1])
            : throw new PythonTypeException($"metadata keys must be str, not '{PythonBuiltins.TypeName(elements[0])}'", nameof(item));
    }
}
