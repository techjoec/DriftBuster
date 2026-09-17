namespace DriftBuster.Backend.Registry;

/// <summary><c>registry.scan.RegistryApp</c>: one installed application read from an Uninstall key.</summary>
/// <param name="DisplayName">The stripped <c>DisplayName</c> value.</param>
/// <param name="KeyPath">The Uninstall subkey path under the hive.</param>
/// <param name="Hive"><c>HKLM</c> or <c>HKCU</c>.</param>
/// <param name="Publisher"><c>str(Publisher)</c> when truthy.</param>
/// <param name="Version"><c>str(DisplayVersion)</c> when truthy.</param>
/// <param name="UninstallString"><c>str(UninstallString)</c> when truthy.</param>
/// <param name="InstallLocation"><c>str(InstallLocation)</c> when truthy.</param>
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
