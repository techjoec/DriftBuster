namespace DriftBuster.Backend.Registry;

/// <summary>
/// <c>registry.scan._Backend</c>: read-only enumeration of a key's subkey names and its values. A key that cannot be opened lists
/// nothing. A value's data is in the <see cref="Infrastructure.EngineJson"/> domain plus <see cref="byte"/> arrays for <c>bytes</c>,
/// as <see cref="WinRegistryValueConverter"/> produces it.
/// </summary>
public interface IRegistryBackend
{
    /// <summary>The subkey names of <paramref name="path"/> under <paramref name="hive"/> in <paramref name="view"/>.</summary>
    IReadOnlyList<string> EnumSubkeys(string hive, string path, string? view);

    /// <summary>The <c>(name, data)</c> pairs of <paramref name="path"/> under <paramref name="hive"/> in <paramref name="view"/>.</summary>
    IReadOnlyList<KeyValuePair<string, object?>> EnumValues(string hive, string path, string? view);
}
