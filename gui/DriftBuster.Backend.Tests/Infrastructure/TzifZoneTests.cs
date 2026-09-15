using System.Buffers.Binary;
using System.Text;

using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Tests.Infrastructure;

/// <summary>
/// <see cref="TzifZone"/> over crafted version 2 TZif files with no transitions, one UTC type and the footer under test, against CPython
/// 3.13's C <c>zoneinfo.ZoneInfo.from_file</c> for the same bytes: <c>utcoffset</c> of each local moment with fold 0 and 1, and the offset
/// and fold <c>astimezone</c> gives for the same fields read as UTC ("l0/l1/utc offset/fold"), or the <c>ValueError</c> the footer raises.
/// </summary>
public sealed class TzifZoneTests
{
    private static readonly (int Year, int Month, int Day, int Hour, int Minute)[] Moments =
    [
        (2023, 3, 1, 1, 30), (2024, 2, 29, 23, 0), (2024, 3, 1, 1, 59), (2024, 3, 10, 2, 30), (2024, 10, 27, 1, 30), (2024, 11, 3, 1, 30),
        (2025, 3, 27, 23, 30), (2025, 12, 31, 23, 59), (2024, 3, 31, 0, 30),
    ];

    [Theory]
    [InlineData("std-dst-default", "EST5EDT,M3.2.0,M11.1.0", "-18000/-18000/-18000/0 -18000/-18000/-18000/0 -18000/-18000/-18000/0 -18000/-14400/-18000/0 -14400/-14400/-14400/0 -14400/-18000/-14400/0 -14400/-14400/-14400/0 -18000/-18000/-18000/0 -14400/-14400/-14400/0")]
    [InlineData("julian", "<+03>-3<+04>,J60/-2,J300/167", "14400/14400/14400/0 14400/14400/14400/0 14400/14400/14400/0 14400/14400/14400/0 14400/14400/14400/0 10800/10800/10800/0 14400/14400/14400/0 10800/10800/10800/0 14400/14400/14400/0")]
    [InlineData("zero-based-day", "AAA3BBB,59/24,365/0", "-7200/-7200/-10800/0 -7200/-7200/-7200/0 -7200/-7200/-7200/0 -7200/-7200/-7200/0 -7200/-7200/-7200/0 -7200/-7200/-7200/0 -7200/-7200/-7200/0 -10800/-10800/-10800/0 -7200/-7200/-7200/0")]
    [InlineData("negative-dst", "IST-1GMT0,M10.5.0,M3.5.0/1", "0/0/0/0 0/0/0/0 0/0/0/0 0/0/0/0 3600/0/0/1 0/0/0/0 0/0/0/0 0/0/0/0 0/0/0/0")]
    [InlineData("seconds-offset", "LMT-0:44:30", "2670/2670/2670/0 2670/2670/2670/0 2670/2670/2670/0 2670/2670/2670/0 2670/2670/2670/0 2670/2670/2670/0 2670/2670/2670/0 2670/2670/2670/0 2670/2670/2670/0")]
    [InlineData("hour-26", "IST-2IDT,M3.4.4/26,M10.5.0", "7200/7200/7200/0 7200/7200/7200/0 7200/7200/7200/0 7200/7200/7200/0 10800/7200/7200/0 7200/7200/7200/0 7200/7200/7200/0 7200/7200/7200/0 10800/10800/10800/0")]
    [InlineData("minus-one", "<-02>2<-01>,M3.5.0/-1,M10.5.0/0", "-7200/-7200/-7200/0 -7200/-7200/-7200/0 -7200/-7200/-7200/0 -7200/-7200/-7200/0 -7200/-7200/-7200/1 -7200/-7200/-7200/0 -7200/-7200/-7200/0 -7200/-7200/-7200/0 -3600/-3600/-7200/0")]
    [InlineData("bad-hour", "AAA3BBB,J60/168,J300", "ValueError: Malformed transition rule in TZ string: b'AAA3BBB,J60/168,J300'")]
    [InlineData("bad-offset", "AAA25", "ValueError: Invalid STD offset in b'AAA25'")]
    [InlineData("missing-rule", "AAA3BBB", "ValueError: Invalid DST offset in b'AAA3BBB'")]
    [InlineData("extra", "AAA3BBB,M3.2.0,M11.1.0x", "ValueError: Extraneous characters at end of TZ string: b'AAA3BBB,M3.2.0,M11.1.0x'")]
    [InlineData("julian-zero", "AAA3BBB,J0,J300", "ValueError: Malformed transition rule in TZ string: b'AAA3BBB,J0,J300'")]
    public void FooterRulesMatchZoneInfo(string name, string footer, string expected)
    {
        TzifZone zone;
        try
        {
            zone = TzifZone.Load(Tzif(footer));
        }
        catch (PythonValueException exc)
        {
            ("ValueError: " + exc.Message).Should().Be(expected, name);
            return;
        }

        var rows = Moments.Select(moment =>
        {
            var seconds = (long)(new DateTime(moment.Year, moment.Month, moment.Day, moment.Hour, moment.Minute, 0, DateTimeKind.Utc) - DateTime.UnixEpoch).TotalSeconds;
            var (offset, fold) = zone.FromUtc(seconds, moment.Year);
            return $"{zone.UtcOffset(seconds, 0, moment.Year)}/{zone.UtcOffset(seconds, 1, moment.Year)}/{offset}/{(fold ? 1 : 0)}";
        });
        string.Join(' ', rows).Should().Be(expected, name);
    }

    [Fact]
    public void MalformedFilesRaiseValueError()
    {
        var valid = Tzif("UTC0");
        FluentActions.Invoking(() => TzifZone.Load("TZXX"u8.ToArray())).Should().Throw<PythonValueException>().WithMessage("Invalid TZif file: magic not found");
        FluentActions.Invoking(() => TzifZone.Load(valid[..^1])).Should().Throw<PythonValueException>().WithMessage("Invalid TZif file: unexpected end of file");
        FluentActions.Invoking(() => TzifZone.Load(valid[..50])).Should().Throw<PythonValueException>();
        var noTypes = Tzif(string.Empty, types: 0);
        FluentActions.Invoking(() => TzifZone.Load(noTypes)).Should().Throw<PythonValueException>().WithMessage("No time zone information found.");
    }

    // A version 2 file: the version 1 block and the version 2 block each with no transitions and `types` UTC types named "UTC", then the footer line.
    private static byte[] Tzif(string footer, int types = 1)
    {
        var bytes = new List<byte>();
        for (var block = 0; block < 2; block++)
        {
            bytes.AddRange("TZif2"u8.ToArray());
            bytes.AddRange(new byte[15]);
            var counts = new byte[24];
            BinaryPrimitives.WriteInt32BigEndian(counts.AsSpan(16), types);
            BinaryPrimitives.WriteInt32BigEndian(counts.AsSpan(20), types == 0 ? 0 : 4);
            bytes.AddRange(counts);
            for (var type = 0; type < types; type++)
            {
                bytes.AddRange(new byte[6]);
            }

            if (types > 0)
            {
                bytes.AddRange("UTC\0"u8.ToArray());
            }
        }

        bytes.Add((byte)'\n');
        bytes.AddRange(Encoding.ASCII.GetBytes(footer));
        bytes.Add((byte)'\n');
        return bytes.ToArray();
    }
}
