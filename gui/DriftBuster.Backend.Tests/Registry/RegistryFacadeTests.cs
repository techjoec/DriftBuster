using DriftBuster.Backend.Models;
using DriftBuster.Backend.Registry;

namespace DriftBuster.Backend.Tests.Registry;

/// <summary>
/// The registry operations of <see cref="DriftbusterBackend"/>: off Windows both fail with the default registry backend's
/// <c>RuntimeError</c> text before anything else is read.
/// </summary>
[Collection(RegistrySeamCollection.Name)]
public sealed class RegistryFacadeTests
{
    [Fact]
    public async Task RegistryCallsFailOffWindowsWithTheDefaultBackendMessage()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The default registry backend reads the live registry on Windows.");
        var ct = TestContext.Current.CancellationToken;
        IDriftbusterBackend backend = new DriftbusterBackend();
        try
        {
            await FluentActions.Awaiting(() => backend.ListRegistryAppsAsync(ct))
                .Should().ThrowAsync<PlatformNotSupportedException>().WithMessage("Windows Registry scanning requires Windows platform");
            await FluentActions.Awaiting(() => backend.SearchRegistryAsync(new RegistrySearchRequest { Token = "Vendor", Roots = ["not a root"], Patterns = ["("] }, ct))
                .Should().ThrowAsync<PlatformNotSupportedException>().WithMessage("Windows Registry scanning requires Windows platform");
        }
        finally
        {
            RegistryOperations.RegistrySummary(reset: true);
        }
    }
}
