using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Registry;

namespace DriftBuster.Backend.Tests.Registry;

/// <summary>
/// Restores every registry seam on dispose, so each test's swaps end with the test.
/// </summary>
internal sealed class RegistrySeams : IDisposable
{
    private readonly Func<bool> _scanIsWindows = RegistryScan.IsWindowsProbe;
    private readonly Func<bool> _commandsIsWindows = RegistryCommands.IsWindows;
    private readonly Func<IReadOnlyList<RegistryApp>> _commandsEnumerate = RegistryCommands.EnumerateInstalledApps;
    private readonly Func<string, IReadOnlyList<RegistryApp>, IReadOnlyList<RegistryRoot>> _commandsFind = RegistryCommands.FindAppRegistryRoots;
    private readonly Func<IReadOnlyList<RegistryRoot>, SearchSpec, IReadOnlyList<RegistryHit>> _commandsSearch = RegistryCommands.SearchRegistry;
    private readonly Func<bool> _collectorIsWindows = RegistryScanCollector.IsWindows;
    private readonly Func<IReadOnlyList<RegistryApp>> _collectorEnumerate = RegistryScanCollector.EnumerateInstalledApps;
    private readonly Func<string, IReadOnlyList<RegistryApp>, IReadOnlyList<RegistryRoot>> _collectorFind = RegistryScanCollector.FindAppRegistryRoots;
    private readonly Func<IReadOnlyList<RegistryRoot>, SearchSpec, IReadOnlyList<RegistryHit>> _collectorSearch = RegistryScanCollector.SearchRegistry;

    public void Dispose()
    {
        RegistryScan.IsWindowsProbe = _scanIsWindows;
        RegistryCommands.IsWindows = _commandsIsWindows;
        RegistryCommands.EnumerateInstalledApps = _commandsEnumerate;
        RegistryCommands.FindAppRegistryRoots = _commandsFind;
        RegistryCommands.SearchRegistry = _commandsSearch;
        RegistryScanCollector.IsWindows = _collectorIsWindows;
        RegistryScanCollector.EnumerateInstalledApps = _collectorEnumerate;
        RegistryScanCollector.FindAppRegistryRoots = _collectorFind;
        RegistryScanCollector.SearchRegistry = _collectorSearch;
    }
}
