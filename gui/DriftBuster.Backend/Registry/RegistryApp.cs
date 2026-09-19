namespace DriftBuster.Backend.Registry;

/// <summary>One installed application from an Uninstall key.</summary>
/// <param name="DisplayName">The trimmed <c>DisplayName</c>.</param>
/// <param name="KeyPath">The Uninstall subkey path under the hive.</param>
/// <param name="Hive"><c>HKLM</c> or <c>HKCU</c>.</param>
/// <param name="Publisher">Publisher, when set.</param>
/// <param name="Version">DisplayVersion, when set.</param>
/// <param name="UninstallString">UninstallString, when set.</param>
/// <param name="InstallLocation">InstallLocation, when set.</param>
/// <param name="View"><c>"32"</c>, <c>"64"</c> or <c>"auto"</c>.</param>
public sealed record RegistryApp(
    string DisplayName,
    string KeyPath,
    string Hive,
    string? Publisher = null,
    string? Version = null,
    string? UninstallString = null,
    string? InstallLocation = null,
    string View = "auto");
