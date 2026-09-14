using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Tests.Infrastructure;

/// <summary><see cref="PythonUtf8.Decode"/> against <c>str(UnicodeDecodeError)</c> from CPython 3.13 <c>bytes.decode("utf-8")</c>.</summary>
public sealed class PythonUtf8Tests
{
    public static TheoryData<byte[], string> Invalid() => new()
    {
        { [0x7B, 0xFF, 0x7D], "'utf-8' codec can't decode byte 0xff in position 1: invalid start byte" },
        { [0x7B, 0x22, 0x61, 0x22, 0x3A, 0x20, 0x22, 0xE0, 0x80, 0x22, 0x7D], "'utf-8' codec can't decode byte 0xe0 in position 7: invalid continuation byte" },
        { [0xED, 0xA0, 0x80], "'utf-8' codec can't decode byte 0xed in position 0: invalid continuation byte" },
        { [0x61, 0x62, 0x63, 0xF0, 0x9F, 0x98], "'utf-8' codec can't decode bytes in position 3-5: unexpected end of data" },
        { [0xF0, 0x9F], "'utf-8' codec can't decode bytes in position 0-1: unexpected end of data" },
        { [0xC3], "'utf-8' codec can't decode byte 0xc3 in position 0: unexpected end of data" },
        { [0xE2, 0x82, 0x78], "'utf-8' codec can't decode bytes in position 0-1: invalid continuation byte" },
        { [0xF4, 0x90, 0x80, 0x80], "'utf-8' codec can't decode byte 0xf4 in position 0: invalid continuation byte" },
        { [0xF0, 0x9F, 0x98, 0x78], "'utf-8' codec can't decode bytes in position 0-2: invalid continuation byte" },
        { [0xC0, 0x80], "'utf-8' codec can't decode byte 0xc0 in position 0: invalid start byte" },
        { [0x6F, 0x6B, 0xE2], "'utf-8' codec can't decode byte 0xe2 in position 2: unexpected end of data" },
    };

    [Theory]
    [MemberData(nameof(Invalid))]
    public void InvalidSequencesRaiseWithPythonsMessage(byte[] bytes, string message)
    {
        var decode = () => PythonUtf8.Decode(bytes);

        decode.Should().Throw<InvalidDataException>().Which.Message.Should().Be(message);
    }

    [Fact]
    public void ValidTextDecodesWithItsByteOrderMarkKept()
    {
        PythonUtf8.Decode([0xEF, 0xBB, 0xBF, 0x7B, 0xF0, 0x9F, 0x98, 0x80, 0x7D]).Should().Be("﻿{\U0001F600}");
        PythonUtf8.DecodeErrorMessage("plain"u8).Should().BeNull();
    }

    // A fact, not a theory: xunit serialises theory arguments, which cannot carry an unpaired surrogate intact.
    [Fact]
    public void HasUnpairedSurrogateFindsTextStrictUtf8CannotEncode()
    {
        PythonUtf8.HasUnpairedSurrogate("plain").Should().BeFalse();
        PythonUtf8.HasUnpairedSurrogate("pair \ud83d\ude00 kept").Should().BeFalse();
        PythonUtf8.HasUnpairedSurrogate("high \ud800 alone").Should().BeTrue();
        PythonUtf8.HasUnpairedSurrogate("low \udfff alone").Should().BeTrue();
        PythonUtf8.HasUnpairedSurrogate("\ude00\ud83d reversed").Should().BeTrue();
        PythonUtf8.HasUnpairedSurrogate("ends high \ud83d").Should().BeTrue();
    }
}
