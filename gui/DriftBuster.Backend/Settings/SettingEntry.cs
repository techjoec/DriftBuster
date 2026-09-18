namespace DriftBuster.Backend.Settings;

/// <summary>One setting: its name as people read it and its value as text.</summary>
public sealed record SettingEntry(string Key, string Value);
