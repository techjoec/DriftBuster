using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Registry;

namespace DriftBuster.Backend.Tests.Registry;

/// <summary>Registry branches the oracle payloads cannot reach from JSON: already-built roots, an empty token and non-JSON list items.</summary>
public sealed class RegistryEdgeTests
{
    [Fact]
    public void NormaliseRootsKeepsBuiltRoots()
    {
        var root = new RegistryRoot("HKLM", "Software", "32");
        OfflineRegistryScanSource.NormaliseRoots(new List<object?> { root, "HKCU\\X" })
            .Should().Equal(root, new RegistryRoot("HKCU", "X"));
    }

    [Theory]
    [InlineData(-1, "registry_registry_-1")]
    [InlineData(7, "registry_registry_07")]
    [InlineData(123, "registry_registry_123")]
    public void DestinationNameForEmptyTokenUsesFallbackIndex(int index, string expected)
        => new OfflineRegistryScanSource(string.Empty).DestinationName(index).Should().Be(expected);

    [Fact]
    public void ListWithItemsWithoutStrHasNoText()
    {
        RegistryScan.ValueText(new List<object?> { "a", new Uri("https://example.invalid") }).Should().BeNull();
        RegistryScan.ValueText(new[] { "a", "b" }).Should().Be("a, b");
    }

    // str() of a list value holding bytes (a non-winreg backend can hand one back): ", ".join(str(x) for x in val) spells b'..' reprs.
    [Fact]
    public void ListValuesHoldingBytesAreSpelledAsPythonStrSpellsThem()
    {
        RegistryScan.ValueText(new List<object?> { new List<object?> { new byte[] { (byte)'x' } }, "z" }).Should().Be("[b'x'], z");

        var backend = new ListDisplayNameBackend();
        var apps = RegistryScan.EnumerateInstalledApps(backend);
        apps.Should().ContainSingle().Which.DisplayName.Should().Be("['a', None, True, b'x']");
    }

    private sealed class ListDisplayNameBackend : IRegistryBackend
    {
        private const string Uninstall = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";

        public IReadOnlyList<string> EnumSubkeys(string hive, string path, string? view)
            => string.Equals(hive, "HKLM", StringComparison.Ordinal) && string.Equals(path, Uninstall, StringComparison.Ordinal) && string.Equals(view, "64", StringComparison.Ordinal)
                ? ["ListName"]
                : [];

        public IReadOnlyList<KeyValuePair<string, object?>> EnumValues(string hive, string path, string? view)
            => string.Equals(path, Uninstall + "\\ListName", StringComparison.Ordinal)
                ? [new("DisplayName", new List<object?> { "a", null, true, new byte[] { (byte)'x' } })]
                : [];
    }

    private static PythonTypeException TypeError(string text) => new("invalid", nameof(text));

    private static PythonIndexException IndexError(string text) => new(nameof(text), "invalid");

    [Fact]
    public void ErrorNamesCoverEveryMappedException()
    {
        RegistryPython.ErrorName(TypeError("x")).Should().Be("TypeError");
        RegistryPython.ErrorName(IndexError("x")).Should().Be("IndexError");
        RegistryPython.ErrorName(new PythonAttributeException()).Should().Be("AttributeError");
        RegistryPython.ErrorName(new PythonRecursionException()).Should().Be("RecursionError");
        RegistryPython.ErrorName(new PythonUnicodeDecodeException()).Should().Be("UnicodeDecodeError");
        RegistryPython.ErrorName(new OverflowException()).Should().Be("OverflowError");
        RegistryPython.ErrorName(new InsufficientMemoryException()).Should().Be("MemoryError");
        RegistryPython.ErrorName(new InvalidOperationException()).Should().Be("RuntimeError");
        RegistryPython.ErrorName(new PythonNotImplementedException()).Should().Be("NotImplementedError");
        RegistryPython.ErrorName(PythonOSError.Create(PythonOSError.PermissionDenied, "x")).Should().Be("PermissionError");
    }
}
