using System.Text.Json.Nodes;

using DriftBuster.Backend.Detection.Plugins;

namespace DriftBuster.Backend.Tests.Detection.Plugins;

/// <summary>
/// The bplist00 reader: top-level keys, value types and invalid payloads.
/// </summary>
public sealed class BinaryPlistTests
{
    private static object? Load(string hex) => BinaryPlist.Load(Convert.FromHexString(hex));

    private static List<string> Keys(string hex) => BinaryPlist.SortedKeys(Load(hex));

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
    public void KeysAreSortedOrdinally()
    {
        var fixture = File.ReadAllBytes(RepoPaths.Fixtures("binary", "preferences.plist"));
        BinaryPlist.SortedKeys(BinaryPlist.Load(fixture)).Should().Equal("Environment", "FeatureFlags", "ReviewedAt");

        var payload = (OrderedDictionary<string, object?>)BinaryPlist.Load(fixture)!;
        payload["Environment"].Should().Be("staging");
        var flags = (OrderedDictionary<string, object?>)payload["FeatureFlags"]!;
        flags["BinaryMode"].Should().Be(true);
        flags["SqliteSync"].Should().Be("v2");
        payload["ReviewedAt"].Should().Be("2025-01-05T09:30:00Z");
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

    // Apple's format keys dicts by strings; a real, date, integer, boolean or null key makes the payload invalid.
    [Theory]
    [InlineData("62706c6973743030d10102223f8000005176080b100000000000000101000000000000000300000000000000000000000000000012")]
    [InlineData("62706c6973743030d10102333ff80000000000005176080b140000000000000101000000000000000300000000000000000000000000000016")]
    [InlineData("62706c6973743030d201020303100210015176080d0f110000000000000101000000000000000400000000000000000000000000000013")]
    [InlineData("62706c6973743030d2010203030910015176080d0e100000000000000101000000000000000400000000000000000000000000000012")]
    [InlineData("62706c6973743030d301020304040410020010015176080f1112140000000000000101000000000000000500000000000000000000000000000016")]
    public void KeysThatAreNotStringsAreInvalid(string hex)
    {
        DecodeErrorType(hex).Should().Be("InvalidDataException");
    }
}
