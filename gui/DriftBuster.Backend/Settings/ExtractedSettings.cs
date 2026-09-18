namespace DriftBuster.Backend.Settings;

/// <summary>
/// A file's settings in the order the file lists them; keys are unique within the file. <paramref name="Truncated"/> is true when
/// the file had more settings than a table can hold.
/// </summary>
public sealed record ExtractedSettings(SettingsMode Mode, IReadOnlyList<SettingEntry> Entries, bool Truncated = false);
