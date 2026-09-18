using System.Numerics;

using DriftBuster.Backend.Detection.Plugins;

namespace DriftBuster.Backend.Tests.Detection.Plugins;

/// <summary>
/// The bplist00 reader: top-level keys, value types and invalid payloads.
/// </summary>
public sealed class BinaryPlistTests
{
    private static object? Load(string hex) => BinaryPlist.Load(Convert.FromHexString(hex));

    private static List<object?> Keys(string hex) => BinaryPlist.SortedKeys(Load(hex));

    private static string DecodeErrorType(string hex)
    {
        var act = () => Load(hex);
        return act.Should().Throw<BinaryPlist.DecodeException>().Which.ErrorType;
    }

    [Theory]
    [InlineData("62706c6973743030a1015178080a000000000000010100000000000000020000000000000000000000000000000c")] // array top
    [InlineData("62706c6973743030d0080000000000000101000000000000000100000000000000000000000000000009")] // empty dict
    public void NonDictAndEmptyPayloadsYieldNoKeys(string hex)
    {
        Keys(hex).Should().BeEmpty();
    }

    [Fact]
    public void KeysAreSortedByCodePoint()
    {
        var fixture = File.ReadAllBytes(RepoPaths.Fixtures("binary", "preferences.plist"));
        BinaryPlist.SortedKeys(BinaryPlist.Load(fixture)).Should().Equal("Environment", "FeatureFlags", "ReviewedAt");

        var payload = (OrderedDictionary<object, object?>)BinaryPlist.Load(fixture)!;
        payload["Environment"].Should().Be("staging");
        var flags = (OrderedDictionary<object, object?>)payload["FeatureFlags"]!;
        flags["BinaryMode"].Should().Be(true);
        flags["SqliteSync"].Should().Be("v2");
        payload["ReviewedAt"].Should().Be("2025-01-05T09:30:00Z");
    }

    [Fact]
    public void FloatAndDateKeysAreRead()
    {
        Keys("62706c6973743030d10102223f8000005176080b100000000000000101000000000000000300000000000000000000000000000012")
            .Should().Equal(1.0);
        Keys("62706c6973743030d10102333ff80000000000005176080b140000000000000101000000000000000300000000000000000000000000000016")
            .Should().Equal(new DateTime(2001, 1, 1, 0, 0, 1, 500, DateTimeKind.Unspecified));
    }

    [Fact]
    public void UnicodeKeysDecodeAsUtf16BigEndian()
    {
        Keys("62706c6973743030d101026200e920ac5176080b100000000000000101000000000000000300000000000000000000000000000012")
            .Should().Equal("\u00e9\u20ac");
    }

    [Theory]
    [InlineData("62706c6973743030d10102a05176080b0c000000000000010100000000000000030000000000000000000000000000000e")] // list as key
    [InlineData("62706c6973743030517808000000000000010100000000000000010000000000000005000000000000000a")] // top object out of range
    [InlineData("62706c6973743030")] // header only
    [InlineData("62706c69737430300000000000000000000000000000000000000000000000000000000000000000")] // zero trailer
    public void InvalidPayloadsRaiseInvalidDataException(string hex)
    {
        DecodeErrorType(hex).Should().Be("InvalidDataException");
        var act = () => Load(hex);
        act.Should().Throw<BinaryPlist.DecodeException>().WithMessage("The binary property list is not valid.");
    }

    [Fact]
    public void IntegerKeysSortNumerically()
    {
        Keys("62706c6973743030d201020303100210015176080d0f110000000000000101000000000000000400000000000000000000000000000013")
            .Should().Equal(new BigInteger(1), new BigInteger(2));
    }

    [Fact]
    public void TrueAndOneAreTheSameKey()
    {
        Keys("62706c6973743030d2010203030910015176080d0e100000000000000101000000000000000400000000000000000000000000000012")
            .Should().Equal(true);
    }

    // The error names the first key's type and the type of the first later key that cannot be ordered against it.
    [Theory]
    [InlineData("62706c6973743030d201020303516110015176080d0f110000000000000101000000000000000400000000000000000000000000000013", "A key of type 'string' cannot be compared with a key of type 'integer'.")]
    [InlineData("62706c6973743030d201020303333ff800000000000051735176080d1618000000000000010100000000000000040000000000000000000000000000001a", "A key of type 'date' cannot be compared with a key of type 'string'.")]
    [InlineData("62706c6973743030d301020304040410020010015176080f1112140000000000000101000000000000000500000000000000000000000000000016", "A key of type 'integer' cannot be compared with a key of type 'null'.")]
    public void MixedKeyTypesAreRejectedBeforeSorting(string hex, string message)
    {
        var act = () => Keys(hex);
        act.Should().Throw<InvalidOperationException>().WithMessage(message);
    }
}
