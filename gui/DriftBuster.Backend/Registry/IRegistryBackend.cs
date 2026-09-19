namespace DriftBuster.Backend.Registry;

/// <summary>
/// Read-only enumeration of a key's subkeys and values; a key that cannot be opened lists nothing. Value data is what
/// <see cref="RegistryValueDecoder"/> produces.
/// </summary>
public interface IRegistryBackend
{
    IReadOnlyList<string> EnumSubkeys(string hive, string path, string? view);

    IReadOnlyList<KeyValuePair<string, object?>> EnumValues(string hive, string path, string? view);
}
