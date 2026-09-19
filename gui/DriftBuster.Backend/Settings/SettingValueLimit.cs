namespace DriftBuster.Backend.Settings;

/// <summary>
/// The longest configuration value DriftBuster compares, in scanned config files and in registry reads. A longer value is left
/// out of the comparison, as are binary registry values: they are data a program keeps, not settings a person reviews.
/// </summary>
public static class SettingValueLimit
{
    /// <summary>
    /// Characters a value may hold and still be compared. A large X.509 certificate chain in PEM (a few certificates with long
    /// name lists) fits with room to spare; counter tables, blobs and embedded stores do not.
    /// </summary>
    public const int MaxChars = 32 * 1024;

    public static bool Fits(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.Length <= MaxChars;
    }
}
