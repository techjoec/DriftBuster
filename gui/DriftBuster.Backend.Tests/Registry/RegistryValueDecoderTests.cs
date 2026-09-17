using System.Numerics;

using DriftBuster.Backend.Registry;

namespace DriftBuster.Backend.Tests.Registry;

/// <summary>
/// <see cref="RegistryValueDecoder"/> and <see cref="WinRegistryKeys"/> on every platform: the value read from raw registry data of
/// each type.
/// </summary>
public sealed class RegistryValueDecoderTests
{
    // UTF-16LE units as they are, an unpaired surrogate included.
    private static byte[] Utf16(string text) => text.SelectMany(unit => new[] { (byte)(unit & 0xFF), (byte)(unit >> 8) }).ToArray();

    [Theory]
    [InlineData("78563412", 0x12345678L)]
    [InlineData("ffffffff", 4294967295L)]
    [InlineData("0100000099", 1L)]
    public void DwordIsLittleEndianUnsigned(string hex, long expected)
    {
        var value = RegistryValueDecoder.Convert(Convert.FromHexString(hex), RegistryValueDecoder.RegDword);
        Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture).Should().Be(expected);
    }

    [Fact]
    public void QwordIsLittleEndianUnsigned()
    {
        RegistryValueDecoder.Convert([], RegistryValueDecoder.RegQword).Should().Be(0);
        RegistryValueDecoder.Convert(Convert.FromHexString("0100000000000000"), RegistryValueDecoder.RegQword).Should().Be(1);
        RegistryValueDecoder.Convert(Convert.FromHexString("0000000001000000"), RegistryValueDecoder.RegQword).Should().Be(4294967296L);
        RegistryValueDecoder.Convert(Convert.FromHexString("ffffffffffffffff"), RegistryValueDecoder.RegQword)
            .Should().Be(BigInteger.Parse("18446744073709551615", System.Globalization.CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData(RegistryValueDecoder.RegSz)]
    [InlineData(RegistryValueDecoder.RegExpandSz)]
    public void StringsStopAtFirstNul(int type)
    {
        RegistryValueDecoder.Convert([], type).Should().Be(string.Empty);
        RegistryValueDecoder.Convert(Utf16("abc\0"), type).Should().Be("abc");
        RegistryValueDecoder.Convert(Utf16("abc"), type).Should().Be("abc");
        RegistryValueDecoder.Convert(Utf16("ab\0cd\0"), type).Should().Be("ab");
        RegistryValueDecoder.Convert(Utf16("%PATH%\0"), type).Should().Be("%PATH%");
        RegistryValueDecoder.Convert([.. Utf16("ab"), 0x41], type).Should().Be("ab");
        RegistryValueDecoder.Convert(Utf16("😀\ud800x\0"), type).Should().Be("😀\ud800x");
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
        RegistryValueDecoder.Convert(Utf16(data), RegistryValueDecoder.RegMultiSz)
            .Should().BeOfType<List<object?>>().Which.Should().Equal(expected);
    }

    [Fact]
    public void EmptyMultiStringIsEmptyList()
    {
        RegistryValueDecoder.Convert([], RegistryValueDecoder.RegMultiSz).Should().BeOfType<List<object?>>().Which.Should().BeEmpty();
        RegistryValueDecoder.Convert([0x61], RegistryValueDecoder.RegMultiSz).Should().BeOfType<List<object?>>().Which.Should().BeEmpty();
    }

    [Theory]
    [InlineData(RegistryValueDecoder.RegNone)]
    [InlineData(RegistryValueDecoder.RegBinary)]
    public void OtherTypesAreBytesOrNone(int type)
    {
        RegistryValueDecoder.Convert([], type).Should().BeNull();
        RegistryValueDecoder.Convert([1, 2, 0], type).Should().BeOfType<byte[]>().Which.Should().Equal(1, 2, 0);
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
