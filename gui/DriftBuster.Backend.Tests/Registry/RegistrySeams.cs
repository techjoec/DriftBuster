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

    public void Dispose()
    {
        RegistryScan.IsWindowsProbe = _scanIsWindows;
        RegistryCommands.IsWindows = _commandsIsWindows;
        RegistryCommands.EnumerateInstalledApps = _commandsEnumerate;
        RegistryCommands.FindAppRegistryRoots = _commandsFind;
        RegistryCommands.SearchRegistry = _commandsSearch;
    }
}
