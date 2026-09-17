namespace DriftBuster.Backend.Tests.Diff;

/// <summary>Casts for the untyped payloads the diff and redaction APIs return.</summary>
internal static class DiffPayloads
{
    public static OrderedDictionary<string, object?> Map(object? value) => (OrderedDictionary<string, object?>)value!;

    public static List<object?> List(object? value) => (List<object?>)value!;
}
