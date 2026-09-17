using System.Numerics;

using DriftBuster.Backend.Registry;

namespace DriftBuster.Backend.Tests.Registry;

/// <summary>
/// <see cref="WinRegistryValueConverter"/> and <see cref="WinRegistryKeys"/> on every platform: the value read from raw registry data of
/// each type.
/// </summary>
public sealed class WinRegistryValueConverterTests
{
    // UTF-16LE units as they are, an unpaired surrogate included.
    private static byte[] Utf16(string text) => text.SelectMany(unit => new[] { (byte)(unit & 0xFF), (byte)(unit >> 8) }).ToArray();

    [Theory]
    [InlineData("78563412", 0x12345678L)]
    [InlineData("ffffffff", 4294967295L)]
    [InlineData("0100000099", 1L)]
    public void DwordIsLittleEndianUnsigned(string hex, long expected)
    {
        var value = WinRegistryValueConverter.Convert(Convert.FromHexString(hex), WinRegistryValueConverter.RegDword);
        Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture).Should().Be(expected);
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
        { "a\0\0b\0", ["a", "", "b"] },
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
}
