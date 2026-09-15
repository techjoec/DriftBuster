using System.Buffers.Binary;
using System.Text;

namespace DriftBuster.Backend.Infrastructure;

/// <summary>
/// The data CPython's C <c>zoneinfo.ZoneInfo</c> builds from a TZif file (<c>zoneinfo._common.load_data</c> then <c>_zoneinfo.c</c>
/// <c>load_data</c>): the UTC transition times, the same times in local wall seconds for each fold, the UTC offset in force after each
/// transition, the offset before the first one (the first standard-time type), and the rule after the last one (the TZ string footer, or
/// the last type). <see cref="UtcOffset"/> and <see cref="FromUtc"/> are <c>find_ttinfo</c> and <c>zoneinfo_fromutc</c>; all times are
/// seconds since 1970-01-01 in whole seconds, so offsets keep their seconds.
/// </summary>
internal sealed class TzifZone
{
    private readonly long[] _transitionsUtc;
    private readonly long[][] _transitionsLocal;
    private readonly long[] _offsets;
    private readonly long _offsetBefore;
    private readonly PosixTzRule _after;

    private TzifZone(long[] transitionsUtc, long[][] transitionsLocal, long[] offsets, long offsetBefore, PosixTzRule after)
    {
        _transitionsUtc = transitionsUtc;
        _transitionsLocal = transitionsLocal;
        _offsets = offsets;
        _offsetBefore = offsetBefore;
        _after = after;
    }

    /// <summary>Builds the zone from the bytes of a TZif file; <see cref="PythonValueException"/> for data zoneinfo refuses.</summary>
    public static TzifZone Load(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var reader = new TzifReader(data);
        var file = reader.ReadData();
        var types = file.UtcOffsets.Length;
        var count = file.TransitionsUtc.Length;
        if (file.TypeIndices.Any(index => index >= types))
        {
            throw new PythonValueException("Invalid transition index found while reading TZif", nameof(data));
        }

        var offsets = file.TypeIndices.Select(index => file.UtcOffsets[index]).ToArray();
        var before = types == 0 ? 0 : file.UtcOffsets[Array.FindIndex(file.IsDst, isDst => !isDst) is var standard and >= 0 ? standard : 0];
        PosixTzRule after;
        if (file.Footer is { Length: > 0 } footer)
        {
            after = PosixTzRule.Parse(footer);
        }
        else if (types == 0)
        {
            throw new PythonValueException("No time zone information found.", nameof(data));
        }
        else
        {
            after = PosixTzRule.Fixed(file.UtcOffsets[count == 0 ? types - 1 : file.TypeIndices[^1]]);
        }

        return new TzifZone(file.TransitionsUtc, LocalTransitions(file), offsets, before, after);
    }

    /// <summary><c>find_ttinfo</c>: the UTC offset in seconds at a local timestamp (seconds since 1970) of <paramref name="year"/> with <paramref name="fold"/>.</summary>
    public long UtcOffset(long localTimestamp, int fold, int year)
    {
        var local = _transitionsLocal[fold];
        var count = local.Length;
        if (count > 0 && localTimestamp < local[0])
        {
            return _offsetBefore;
        }

        if (count == 0 || localTimestamp > local[^1])
        {
            return _after.OffsetAtLocal(localTimestamp, fold, year);
        }

        return _offsets[BisectRight(local, localTimestamp) - 1];
    }

    /// <summary><c>zoneinfo_fromutc</c>: the offset in seconds at a UTC timestamp of <paramref name="year"/>, and whether the local time is the second of a repeated pair.</summary>
    public (long Offset, bool Fold) FromUtc(long utcTimestamp, int year)
    {
        var count = _transitionsUtc.Length;
        if (count >= 1 && utcTimestamp < _transitionsUtc[0])
        {
            return (_offsetBefore, false);
        }

        if (count == 0 || utcTimestamp > _transitionsUtc[^1])
        {
            var (offset, fold) = _after.OffsetAtUtc(utcTimestamp, year);
            if (count > 0)
            {
                var previous = count == 1 ? _offsetBefore : _offsets[count - 2];
                var difference = previous - offset;
                fold |= difference > 0 && utcTimestamp < _transitionsUtc[^1] + difference;
            }

            return (offset, fold);
        }

        var index = BisectRight(_transitionsUtc, utcTimestamp);
        var (before, after) = index >= 2 ? (_offsets[index - 2], _offsets[index - 1]) : (_offsetBefore, _offsets[0]);
        return (after, before - after > utcTimestamp - _transitionsUtc[index - 1]);
    }

    // ts_to_local: each transition in wall seconds, for fold 0 with the larger and for fold 1 with the smaller of the offsets either side.
    private static long[][] LocalTransitions(TzifData file)
    {
        var count = file.TransitionsUtc.Length;
        var local = new[] { (long[])file.TransitionsUtc.Clone(), (long[])file.TransitionsUtc.Clone() };
        if (count == 0)
        {
            return local;
        }

        var offsets = file.UtcOffsets;
        for (var index = 0; index < count; index++)
        {
            var (first, second) = index == 0
                ? (offsets[0], offsets.Length > 1 ? offsets[file.TypeIndices[0]] : offsets[0])
                : (offsets[file.TypeIndices[index - 1]], offsets[file.TypeIndices[index]]);
            local[0][index] += Math.Max(first, second);
            local[1][index] += Math.Min(first, second);
        }

        return local;
    }

    private static int BisectRight(long[] values, long value)
    {
        int low = 0, high = values.Length;
        while (low < high)
        {
            var middle = (low + high) / 2;
            if (values[middle] > value)
            {
                high = middle;
            }
            else
            {
                low = middle + 1;
            }
        }

        return high;
    }

    /// <summary>What <c>_common.load_data</c> returns (abbreviations validated but not kept).</summary>
    private sealed record TzifData(long[] TransitionsUtc, int[] TypeIndices, long[] UtcOffsets, bool[] IsDst, string? Footer);

    /// <summary><c>_common.load_data</c> over a byte array: struct unpacking that runs out of bytes, like a bad header, raises.</summary>
    private sealed class TzifReader(byte[] data)
    {
        private int _position;

        public TzifData ReadData()
        {
            var (version, header) = ReadHeader();
            var timeSize = 4;
            if (version != 1)
            {
                timeSize = 8;
                Skip(checked((header.TimeCount * 5L) + (header.TypeCount * 6L) + header.CharCount + (header.LeapCount * 8L) + header.IsStdCount + header.IsUtcCount));
                (_, header) = ReadHeader();
            }

            if (header.TimeCount > (data.Length - _position) / timeSize)
            {
                throw Invalid("Invalid TZif file: unexpected end of data");
            }

            var transitions = new long[header.TimeCount];
            for (var index = 0; index < transitions.Length; index++)
            {
                var bytes = Take(timeSize);
                transitions[index] = timeSize == 4 ? BinaryPrimitives.ReadInt32BigEndian(bytes) : BinaryPrimitives.ReadInt64BigEndian(bytes);
            }

            var indices = Take(header.TimeCount).ToArray().Select(value => (int)value).ToArray();
            var offsets = new long[header.TypeCount];
            var isDst = new bool[header.TypeCount];
            var abbreviationIndices = new int[header.TypeCount];
            for (var index = 0; index < header.TypeCount; index++)
            {
                var record = Take(6);
                offsets[index] = BinaryPrimitives.ReadInt32BigEndian(record);
                isDst[index] = record[4] != 0;
                abbreviationIndices[index] = (sbyte)record[5];
            }

            ValidateAbbreviations(Take(header.CharCount).ToArray(), abbreviationIndices);
            return new TzifData(transitions, indices, offsets, isDst, version >= 2 ? ReadFooter(header) : null);
        }

        private (int Version, Header Header) ReadHeader()
        {
            if (!Take(4).SequenceEqual("TZif"u8))
            {
                throw Invalid("Invalid TZif file: magic not found");
            }

            var versionByte = Take(1)[0];
            var version = versionByte == 0 ? 1 : char.IsAsciiDigit((char)versionByte) ? versionByte - '0' : throw Invalid("Invalid TZif version");
            Skip(15);
            var counts = Take(24).ToArray();
            return (version, new Header(Count(counts, 0), Count(counts, 1), Count(counts, 2), Count(counts, 3), Count(counts, 4), Count(counts, 5)));
        }

        // struct.unpack(">6l") slot; a negative count makes the next unpack format invalid.
        private static int Count(byte[] counts, int slot)
            => BinaryPrimitives.ReadInt32BigEndian(counts.AsSpan(slot * 4)) is var value and >= 0 ? value : throw Invalid("Invalid TZif file: negative count");

        // The version 2+ footer: past the leap-second and indicator records, a newline, then one line ending with a newline.
        private string ReadFooter(Header header)
        {
            Skip(checked(header.IsUtcCount + header.IsStdCount + (header.LeapCount * 12L)));
            if (_position >= data.Length || data[_position++] != '\n')
            {
                throw Invalid("Invalid TZif file: no newline before the footer");
            }

            var end = Array.IndexOf(data, (byte)'\n', _position);
            if (end < 0)
            {
                throw Invalid("Invalid TZif file: unexpected end of file");
            }

            var line = data.AsSpan(_position, end - _position);
            var terminator = line.IndexOf((byte)0);
            return Encoding.Latin1.GetString(terminator < 0 ? line : line[..terminator]);
        }

        // get_abbr for every type: the bytes from its index (Python slice semantics) to the next NUL must decode as UTF-8.
        private static void ValidateAbbreviations(byte[] characters, int[] indices)
        {
            foreach (var index in indices)
            {
                var start = index < 0 ? Math.Max(0, characters.Length + index) : Math.Min(index, characters.Length);
                var terminator = Array.IndexOf(characters, (byte)0, start);
                var end = terminator < 0 ? Math.Max(0, characters.Length - 1) : terminator;
                if (end > start)
                {
                    _ = PythonUtf8.Decode(characters[start..end]);
                }
            }
        }

        private ReadOnlySpan<byte> Take(int count)
        {
            if (count < 0 || count > data.Length - _position)
            {
                throw Invalid("Invalid TZif file: unexpected end of data");
            }

            var span = data.AsSpan(_position, count);
            _position += count;
            return span;
        }

        private void Skip(long count) => _position = (int)Math.Min(data.Length, _position + count);

        private static PythonValueException Invalid(string message) => new(message, nameof(message));
    }

    private sealed record Header(int IsUtcCount, int IsStdCount, int LeapCount, int TimeCount, int TypeCount, int CharCount);
}
