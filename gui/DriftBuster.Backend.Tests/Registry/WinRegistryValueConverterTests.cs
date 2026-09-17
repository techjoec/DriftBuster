using System.Numerics;

using DriftBuster.Backend.Registry;

namespace DriftBuster.Backend.Tests.Registry;

/// <summary>
/// <see cref="WinRegistryValueConverter"/> (<c>Reg2Py</c>) and <see cref="WinRegistryKeys"/> on every platform: the values
/// <c>winreg.EnumValue</c> returns for raw data of each type, worked through CPython 3.13's <c>PC/winreg.c</c> by hand.
/// </summary>
public sealed class WinRegistryValueConverterTests
{
    // UTF-16LE units as they are, an unpaired surrogate included.
    private static byte[] Utf16(string text) => text.SelectMany(unit => new[] { (byte)(unit & 0xFF), (byte)(unit >> 8) }).ToArray();

    [Theory]
    [InlineData("", 0L)]
    [InlineData("01", 1L)]
    [InlineData("78563412", 0x12345678L)]
    [InlineData("ffffffff", 4294967295L)]
    [InlineData("0100000099", 1L)]
    public void DwordIsLittleEndianUnsigned(string hex, long expected)
    {
        var value = WinRegistryValueConverter.Convert(Convert.FromHexString(hex), WinRegistryValueConverter.RegDword);
        Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture).Should().Be(expected);
    }

    [Fact]
    public void DwordNarrowsLikePythonInts()
    {
        WinRegistryValueConverter.Convert(Convert.FromHexString("2a000000"), WinRegistryValueConverter.RegDword).Should().Be(42);
        WinRegistryValueConverter.Convert(Convert.FromHexString("ffffffff"), WinRegistryValueConverter.RegDword).Should().Be(4294967295L);
    }

    [Fact]
    public void QwordIsLittleEndianUnsigned()
    {
        WinRegistryValueConverter.Convert([], WinRegistryValueConverter.RegQword).Should().Be(0);
        WinRegistryValueConverter.Convert(Convert.FromHexString("0100000000000000"), WinRegistryValueConverter.RegQword).Should().Be(1);
        WinRegistryValueConverter.Convert(Convert.FromHexString("0000000001000000"), WinRegistryValueConverter.RegQword).Should().Be(4294967296L);
        WinRegistryValueConverter.Convert(Convert.FromHexString("ffffffffffffffff"), WinRegistryValueConverter.RegQword)
            .Should().Be(BigInteger.Parse("18446744073709551615", System.Globalization.CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData(WinRegistryValueConverter.RegSz)]
    [InlineData(WinRegistryValueConverter.RegExpandSz)]
    public void StringsStopAtFirstNul(int type)
    {
        WinRegistryValueConverter.Convert([], type).Should().Be(string.Empty);
        WinRegistryValueConverter.Convert(Utf16("abc\0"), type).Should().Be("abc");
        WinRegistryValueConverter.Convert(Utf16("abc"), type).Should().Be("abc");
        WinRegistryValueConverter.Convert(Utf16("ab\0cd\0"), type).Should().Be("ab");
        WinRegistryValueConverter.Convert(Utf16("%PATH%\0"), type).Should().Be("%PATH%");
        WinRegistryValueConverter.Convert([.. Utf16("ab"), 0x41], type).Should().Be("ab");
        WinRegistryValueConverter.Convert(Utf16("😀\ud800x\0"), type).Should().Be("😀\ud800x");
    }

    public static TheoryData<string, string[]> MultiStringCases => new()
    {
        { "a\0b\0\0", ["a", "b"] },
        { "a\0b\0", ["a", "b"] },
        { "a\0b", ["a", "b"] },
        { "a\0\0b\0", ["a", "", "b"] },
        { "\0", [] },
        { "\0\0", [""] },
        { "\0\0\0", ["", ""] },
        { "ab", ["ab"] },
        { "only\0\0\0\0", ["only", "", ""] },
    };

    [Theory]
    [MemberData(nameof(MultiStringCases))]
    public void MultiStringsSplitAtEveryNulLessOneTrailing(string data, string[] expected)
    {
        WinRegistryValueConverter.Convert(Utf16(data), WinRegistryValueConverter.RegMultiSz)
            .Should().BeOfType<List<object?>>().Which.Should().Equal(expected);
    }

    [Fact]
    public void EmptyMultiStringIsEmptyList()
    {
        WinRegistryValueConverter.Convert([], WinRegistryValueConverter.RegMultiSz).Should().BeOfType<List<object?>>().Which.Should().BeEmpty();
        WinRegistryValueConverter.Convert([0x61], WinRegistryValueConverter.RegMultiSz).Should().BeOfType<List<object?>>().Which.Should().BeEmpty();
    }

    [Theory]
    [InlineData(WinRegistryValueConverter.RegNone)]
    [InlineData(WinRegistryValueConverter.RegBinary)]
    [InlineData(WinRegistryValueConverter.RegDwordBigEndian)]
    [InlineData(WinRegistryValueConverter.RegLink)]
    [InlineData(8)]
    [InlineData(99)]
    public void OtherTypesAreBytesOrNone(int type)
    {
        WinRegistryValueConverter.Convert([], type).Should().BeNull();
        WinRegistryValueConverter.Convert([1, 2, 0], type).Should().BeOfType<byte[]>().Which.Should().Equal(1, 2, 0);
    }

    [Fact]
    public void AccessMasksFollowTheView()
    {
        WinRegistryKeys.AccessFor(null).Should().Be(0x20019);
        WinRegistryKeys.AccessFor("auto").Should().Be(0x20019);
        WinRegistryKeys.AccessFor("64").Should().Be(0x20119);
        WinRegistryKeys.AccessFor("32").Should().Be(0x20219);
    }

    [Fact]
    public void HiveHandlesAreThePredefinedKeys()
    {
        ((long)WinRegistryKeys.HiveHandle("HKLM")).Should().Be(unchecked((int)0x80000002));
        ((long)WinRegistryKeys.HiveHandle("HKCU")).Should().Be(unchecked((int)0x80000001));
        var act = () => WinRegistryKeys.HiveHandle("hklm");
        act.Should().Throw<KeyNotFoundException>().WithMessage("'hklm'");
    }
}
