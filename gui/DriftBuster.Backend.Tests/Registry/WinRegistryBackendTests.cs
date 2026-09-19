using System.Runtime.Versioning;

using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Registry;

using Microsoft.Win32;

namespace DriftBuster.Backend.Tests.Registry;

/// <summary>
/// <see cref="WinRegistryBackend"/> against the real registry, Windows only: a scratch key under <c>HKCU\Software</c> holds one value of
/// each type and two subkeys, written through <see cref="RegistryKey"/> and read back through the backend.
/// </summary>
[Collection(RegistrySeamCollection.Name)]
public sealed class WinRegistryBackendTests : IDisposable
{
    private const string SkipReason = "the Windows registry exists only on Windows";

    private readonly string _keyPath = $@"Software\DriftBusterTests\{Guid.NewGuid():N}";

    public void Dispose()
    {
        if (OperatingSystem.IsWindows())
        {
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(_keyPath, throwOnMissingSubKey: false);
        }
    }

    [SupportedOSPlatform("windows")]
    private void CreateScratchKey()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(_keyPath);
        key.SetValue("Sz", "text", RegistryValueKind.String);
        key.SetValue("Expand", "%WINDIR%\\x", RegistryValueKind.ExpandString);
        key.SetValue("Multi", new[] { "a", "b" }, RegistryValueKind.MultiString);
        key.SetValue("Dword", unchecked((int)0xFFFFFFFF), RegistryValueKind.DWord);
        key.SetValue("Qword", 5L, RegistryValueKind.QWord);
        key.SetValue("Binary", new byte[] { 1, 2, 3 }, RegistryValueKind.Binary);
        key.SetValue("EmptyBinary", Array.Empty<byte>(), RegistryValueKind.Binary);
        key.CreateSubKey("Beta").Dispose();
        key.CreateSubKey("Alpha").Dispose();
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void EnumValuesConvertsEachType()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), SkipReason);
        CreateScratchKey();
        var values = new WinRegistryBackend().EnumValues("HKCU", _keyPath, null).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

        values["Sz"].Should().Be("text");
        values["Expand"].Should().Be("%WINDIR%\\x");
        values["Multi"].Should().BeOfType<List<object?>>().Which.Should().Equal("a", "b");
        values["Dword"].Should().Be(4294967295L);
        values["Qword"].Should().Be(5);
        values["Binary"].Should().BeOfType<byte[]>().Which.Should().Equal(1, 2, 3);
        values["EmptyBinary"].Should().BeNull();
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void EnumSubkeysListsChildrenAndMissingKeysListNothing()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), SkipReason);
        CreateScratchKey();
        var backend = new WinRegistryBackend();

        backend.EnumSubkeys("HKCU", _keyPath, "64").Should().BeEquivalentTo(["Alpha", "Beta"]);
        backend.EnumSubkeys("HKCU", _keyPath + @"\Missing", null).Should().BeEmpty();
        backend.EnumValues("HKCU", _keyPath + @"\Missing", "32").Should().BeEmpty();
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void DefaultBackendIsWindowsBackendOnWindows()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), SkipReason);
        RegistryScan.DefaultBackend().Should().BeOfType<WinRegistryBackend>();
    }

    [Fact]
    public void DefaultBackendRaisesOffWindows()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "the default backend exists on Windows");
        var act = () => RegistryScan.DefaultBackend();
        act.Should().Throw<PlatformNotSupportedException>().WithMessage("Windows Registry scanning requires Windows platform");
    }
}
