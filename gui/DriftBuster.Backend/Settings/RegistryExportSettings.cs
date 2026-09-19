using DriftBuster.Backend.Detection.Plugins;

namespace DriftBuster.Backend.Settings;

/// <summary>
/// Registry Editor export settings: each value named <c>[key path] value name</c> (<c>(Default)</c> for <c>@</c>), with its data
/// as <see cref="RegistryExportText.RenderData"/> shows it. A <c>[-key]</c> section is one <c>[key] (key)</c> row reading
/// <c>(deleted)</c>. Binary values (<see cref="RegistryExportText.IsSettingData"/>) are left out. Null when the text has no export
/// header, so the caller falls back to the line reader.
/// </summary>
internal static class RegistryExportSettings
{
    internal const string KeyRowName = "(key)";

    public static ExtractedSettings? Extract(string text)
    {
        var version = RegistryExportText.HeaderVersion(text);
        if (version is null)
        {
            return null;
        }

        var unicode = !string.Equals(version, "4", StringComparison.Ordinal);
        var builder = new SettingsBuilder();
        foreach (var entry in RegistryExportText.Entries(text))
        {
            if (builder.IsFull)
            {
                break;
            }

            if (entry.ValueName is null)
            {
                if (entry.KeyDeleted)
                {
                    builder.Add($"[{entry.KeyPath}] {KeyRowName}", "(deleted)");
                }

                continue;
            }

            if (RegistryExportText.IsSettingData(entry.RawData!))
            {
                builder.Add($"[{entry.KeyPath}] {entry.ValueName}", RegistryExportText.RenderData(entry.RawData!, unicode));
            }
        }

        return builder.Build(SettingsMode.Parsed);
    }
}
