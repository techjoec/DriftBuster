using System.Numerics;

using DriftBuster.Backend.Detection.Plugins;

namespace DriftBuster.Backend.Tests.Detection.Plugins;

/// <summary>
/// The bplist00 reader accepts and rejects exactly what plistlib does; every blob and outcome below was produced
/// and checked with the interpreter (plistlib.load over the same bytes).
/// </summary>
public sealed class BinaryPlistTests
{
    private static object? Load(string hex) => BinaryPlist.Load(Convert.FromHexString(hex));

    private static List<object?> Keys(string hex) => BinaryPlist.SortedKeys(Load(hex));

    private static string DecodeErrorType(string hex)
    {
        var act = () => Load(hex);
        return act.Should().Throw<BinaryPlist.DecodeException>().Which.PythonType;
    }

    [Theory]
    [InlineData("62706c6973743030a1015178080a000000000000010100000000000000020000000000000000000000000000000c")] // array top
    [InlineData("62706c6973743030a10008000000000000010100000000000000010000000000000000000000000000000a")] // array containing itself
    [InlineData("62706c69737430304f1002abcd08000000000000010100000000000000010000000000000000000000000000000d")] // data with extended size
    [InlineData("62706c6973743030130108000000000000010100000000000000010000000000000000000000000000000a")] // short int read
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

    [Fact]
    public void FloatAndDateKeysAreRead()
    {
        Keys("62706c6973743030d10102223f8000005176080b100000000000000101000000000000000300000000000000000000000000000012")
            .Should().Equal(1.0);
        Keys("62706c6973743030d10102333ff80000000000005176080b140000000000000101000000000000000300000000000000000000000000000016")
            .Should().Equal(new DateTime(2001, 1, 1, 0, 0, 1, 500, DateTimeKind.Unspecified));
    }

    // timedelta(seconds=f) converts the integer part exactly and rounds only the fraction, half to even.
    [Theory]
    [InlineData("41c4dc93800fcd6c", "2023-03-08T20:26:40.123456")]
    [InlineData("bec4f8b588e368f1", "2000-12-31T23:59:59.999998")]
    public void DateKeysRoundToMicrosecondsAsTimedeltaDoes(string seconds, string expected)
    {
        var keys = Keys("62706c6973743030d1010233" + seconds + "5176080b140000000000000101000000000000000300000000000000000000000000000016");
        keys.Should().Equal(DateTime.ParseExact(expected, "yyyy-MM-dd'T'HH:mm:ss.ffffff", System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void UnicodeKeysDecodeAsUtf16BigEndian()
    {
        Keys("62706c6973743030d101026200e920ac5176080b100000000000000101000000000000000300000000000000000000000000000012")
            .Should().Equal("\u00e9\u20ac");
    }

    // The TypeError names the operands of the first comparison the sort makes between incomparable keys.
    [Theory]
    [InlineData("62706c6973743030d201020303516110015176080d0f110000000000000101000000000000000400000000000000000000000000000013", "'<' not supported between instances of 'int' and 'str'")]
    [InlineData("62706c6973743030d201020303333ff800000000000051735176080d1618000000000000010100000000000000040000000000000000000000000000001a", "'<' not supported between instances of 'str' and 'datetime.datetime'")]
    [InlineData("62706c6973743030d301020304040410020010015176080f1112140000000000000101000000000000000500000000000000000000000000000016", "'<' not supported between instances of 'NoneType' and 'int'")]
    public void MixedKeyTypesRaiseTheSortTypeError(string hex, string message)
    {
        var act = () => Keys(hex);
        act.Should().Throw<InvalidOperationException>().WithMessage(message);
    }

    [Fact]
    public void NaNKeysSortWhereTheInterpreterLeavesThem()
    {
        // sorted([1.0, nan]) keeps the order: nan < 1.0 is false, so the first compare finds an ascending run.
        var keys = Keys("62706c6973743030d201020303233ff0000000000000237ff800000000000051760008000d0016001f0000000000000201000000000000000400000000000000000000000000000021");
        keys.Should().HaveCount(2);
        keys[0].Should().Be(1.0);
        keys[1].Should().BeOfType<double>().Which.Should().Be(double.NaN);
    }

    [Theory]
    [InlineData("62706c6973743030d10102a05176080b0c000000000000010100000000000000030000000000000000000000000000000e")] // list as key
    [InlineData("62706c697374303051ff08000000000000010100000000000000010000000000000000000000000000000a")] // non-ASCII in ascii string
    [InlineData("62706c6973743030d1010261d8005176080b0e0000000000000101000000000000000300000000000000000000000000000010")] // lone surrogate
    [InlineData("62706c69737430308fffffffffffffffffffffffffffffffff080000000000000101000000000000000100000000000000000000000000000019")] // UID >= 2**64
    [InlineData("62706c6973743030a0080000000000000100000000000000000100000000000000000000000000000009")] // ref size 0
    [InlineData("62706c6973743030337ff0000000000000080000000000000101000000000000000100000000000000000000000000000011")] // infinite date
    [InlineData("62706c6973743030517808000000000000010100000000000000010000000000000005000000000000000a")] // top object out of range
    [InlineData("62706c697374303070080000000000000101000000000000000100000000000000000000000000000009")] // unknown token
    [InlineData("62706c6973743030")] // header only
    [InlineData("62706c69737430300000000000000000000000000000000000000000000000000000000000000000")] // zero trailer
    public void InvalidPayloadsRaiseInvalidFileException(string hex)
    {
        DecodeErrorType(hex).Should().Be("InvalidFileException");
        var act = () => Load(hex);
        act.Should().Throw<BinaryPlist.DecodeException>().WithMessage("Invalid file");
    }

    private static byte[] NestedArrays(int depth, byte[]? leaf = null)
    {
        var objects = new List<byte[]>();
        for (var index = 0; index < depth; index++)
        {
            var next = index + 1;
            objects.Add([0xA1, (byte)(next >> 8), (byte)next]);
        }

        objects.Add(leaf ?? [0xA0]);

        var output = new List<byte>("bplist00"u8.ToArray());
        var offsets = new List<int>();
        foreach (var item in objects)
        {
            offsets.Add(output.Count);
            output.AddRange(item);
        }

        var table = output.Count;
        foreach (var offset in offsets)
        {
            output.Add((byte)(offset >> 8));
            output.Add((byte)offset);
        }

        var trailer = new byte[32];
        trailer[6] = 2;
        trailer[7] = 2;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(trailer.AsSpan(8), (ulong)objects.Count);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(trailer.AsSpan(24), (ulong)table);
        output.AddRange(trailer);
        return output.ToArray();
    }

    // Under the parity dump (parse() at frame 8) plistlib reads 988 nested arrays around an empty one: the innermost
    // _read sits at frame 1000. One more array pushes frame 1001 and raises RecursionError.
    [Fact]
    public void DeepNestingRaisesRecursionErrorAtTheInterpretersFrameLimit()
    {
        BinaryPlist.SortedKeys(BinaryPlist.Load(NestedArrays(988))).Should().BeEmpty();

        AssertRecursionError(NestedArrays(989));
        AssertRecursionError(NestedArrays(5000));
    }

    // A reference count too large for the payload fails inside _read, whose InvalidFileException.__init__ frame is
    // one deeper than _read: at 987 arrays that frame is 1000 and the error is InvalidFileException, at 988 it would be
    // frame 1001, so the interpreter raises RecursionError instead. An int leaf needs no frame beyond _read_object.
    [Fact]
    public void ErrorsRaisedAtTheFrameLimitBecomeRecursionErrors()
    {
        byte[] hugeRefCount = [0xAF, 0x13, 0x7F, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF];
        DecodeErrorType(Convert.ToHexString(NestedArrays(987, hugeRefCount))).Should().Be("InvalidFileException");
        AssertRecursionError(NestedArrays(988, hugeRefCount));

        BinaryPlist.Load(NestedArrays(989, [0x10, 0x01])).Should().BeOfType<List<object?>>();
        AssertRecursionError(NestedArrays(990, [0x10, 0x01]));
    }

    private static void AssertRecursionError(byte[] payload)
    {
        var act = () => BinaryPlist.Load(payload);
        var error = act.Should().Throw<BinaryPlist.DecodeException>().Which;
        error.PythonType.Should().Be("RecursionError");
        error.Message.Should().Be("maximum recursion depth exceeded");
    }
}
