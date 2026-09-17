using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Infrastructure.PythonRe;
using DriftBuster.Backend.Registry;

namespace DriftBuster.Backend.Tests.Registry;

/// <summary><see cref="RegistryPython"/>'s built-ins and the small <see cref="RegistryRoot"/> surface, against CPython 3.13 values.</summary>
public sealed class RegistryPythonTests
{
    [Theory]
    [InlineData("", "b''")]
    [InlineData("616263", "b'abc'")]
    [InlineData("69742773", "b\"it's\"")]
    [InlineData("612762220b7f5c", "b'a\\'b\"\\x0b\\x7f\\\\'")]
    [InlineData("090a0d00ff", "b'\\t\\n\\r\\x00\\xff'")]
    public void BytesReprMatchesPython(string hex, string expected)
        => PythonRepr.Str(Convert.FromHexString(hex)).Should().Be(expected);

    [Fact]
    public void UpperUsesFullCaseMapping()
    {
        RegistryPython.Upper("straße ﬁ ı a\ud800").Should().Be("STRASSE FI I A\ud800");
    }

    [Fact]
    public void SplitKeepsEmptyEdgesAndCountsCodePoints()
    {
        var pattern = PythonPattern.Compile(@"[\s_-]+");
        RegistryPython.Split(pattern, " a__\U0001f600-b ").Should().Equal("", "a", "\U0001f600", "b", "");
        RegistryPython.Split(pattern, string.Empty).Should().Equal(string.Empty);
    }

    private static PythonValueException ValueError(string text) => new("invalid", nameof(text));

    [Fact]
    public void ErrorNamesFollowPython()
    {
        RegistryPython.ErrorName(ValueError("x")).Should().Be("ValueError");
        RegistryPython.ErrorName(new PlatformNotSupportedException()).Should().Be("RuntimeError");
        RegistryPython.ErrorName(new KeyNotFoundException()).Should().Be("KeyError");
        RegistryPython.ErrorName(new CommandExitException("x")).Should().Be("SystemExit");
        RegistryPython.ErrorName(new PythonReException("x")).Should().Be("PatternError");
        RegistryPython.ErrorName(new PythonNotImplementedException("x")).Should().Be("NotImplementedError");
        RegistryPython.ErrorName(new IOException("x")).Should().Be("OSError");
        RegistryPython.ErrorName(new FormatException()).Should().Be("FormatException");
    }

    [Fact]
    public void RootAsTupleAndEquality()
    {
        var root = new RegistryRoot("HKLM", "Software", "64");
        root.AsTuple().Should().Be(("HKLM", "Software", "64"));
        root.Should().Be(new RegistryRoot("HKLM", "Software", "64"));
        root.Should().NotBe(new RegistryRoot("HKLM", "software", "64"));
        var act = () => RegistryRoot.Parse(null);
        act.Should().Throw<PythonValueException>().WithMessage("Registry root descriptor must be non-empty");
    }
}
