using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace DriftBuster.Backend.Detection.Plugins;

/// <summary>
/// Reader for <c>bplist00</c>: trailer, offset table and object table (null, bools, integers, reals, dates, data, ASCII and
/// UTF-16BE strings, UIDs, arrays, dicts). Objects are cached by reference, so shared and self-referencing containers terminate.
/// </summary>
/// <remarks>
/// Values decode to null, bool, BigInteger, double, DateTime, byte[], string, <see cref="Uid"/>, List and OrderedDictionary with
/// string keys (a dict key that is not a string, as Apple's format requires, makes the payload invalid). Every malformed payload throws one
/// <see cref="DecodeException"/>; nesting past <see cref="MaxNestingDepth"/> does too, instead of exhausting the stack.
/// </remarks>
internal static partial class BinaryPlist
{
    /// <summary>The deepest chain of nested containers the reader follows before it gives up on a payload.</summary>
    internal const int MaxNestingDepth = 1000;

    /// <summary>A decode failure and the exception type it stands for.</summary>
    internal sealed class DecodeException(string errorType, string message) : Exception(message)
    {
        public string ErrorType { get; } = errorType;
    }

    /// <summary>A plist UID; its string form is <c>UID(7)</c>.</summary>
    internal sealed record Uid(BigInteger Data)
    {
        public override string ToString() => $"UID({Data.ToString(CultureInfo.InvariantCulture)})";
    }

    private static readonly Encoding StrictUtf16Be = new UnicodeEncoding(bigEndian: true, byteOrderMark: false, throwOnInvalidBytes: true);

    private static readonly BigInteger UidLimit = BigInteger.One << 64;

    // Offsets from the plist epoch that stay inside years 1-9999, which is the range a date value may land in.
    private const long MinDateMicroseconds = -63113904000L * 1_000_000;
    private const long MaxDateMicrosecondsExclusive = 252423993600L * 1_000_000;
    private static readonly DateTime PlistEpoch = new(2001, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);

    // Every kind of damage reports the same failure.
    private static DecodeException InvalidFile() => new(nameof(InvalidDataException), "The binary property list is not valid.");

    private static DecodeException TooDeep()
        => new(nameof(InvalidDataException), string.Create(CultureInfo.InvariantCulture, $"The binary property list is nested more than {MaxNestingDepth} levels deep."));

    /// <summary>Reads a bplist00 payload.</summary>
    /// <exception cref="DecodeException">The payload is malformed or nested past <see cref="MaxNestingDepth"/>.</exception>
    public static object? Load(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return new Parser(data).Parse();
    }

    private sealed class Parser(byte[] data)
    {
        private static readonly object Undefined = new();

        private readonly byte[] _data = data;
        private int _pos;
        private int _refSize;
        private ulong[] _offsets = [];
        private object?[] _objects = [];

        private int Remaining => _data.Length - _pos;

        public object? Parse()
        {
            if (_data.Length < 32)
            {
                throw InvalidFile();
            }

            var trailer = _data.AsSpan(_data.Length - 32);
            int offsetSize = trailer[6];
            _refSize = trailer[7];
            var numObjects = BinaryPrimitives.ReadUInt64BigEndian(trailer[8..]);
            var topObject = BinaryPrimitives.ReadUInt64BigEndian(trailer[16..]);
            var tableOffset = BinaryPrimitives.ReadUInt64BigEndian(trailer[24..]);
            Seek(tableOffset);
            _offsets = ReadInts(numObjects, offsetSize);
            _objects = new object?[_offsets.Length];
            Array.Fill(_objects, Undefined);
            return ReadObject(topObject, depth: 1);
        }

        // A seek past the end lands at the end, where every read is empty.
        private void Seek(ulong offset) => _pos = offset >= (ulong)_data.Length ? _data.Length : (int)offset;

        // Exactly size bytes, or a malformed payload.
        private ReadOnlySpan<byte> Read(ulong size)
        {
            if (size > (ulong)Remaining)
            {
                throw InvalidFile();
            }

            var span = _data.AsSpan(_pos, (int)size);
            _pos += (int)size;
            return span;
        }

        // Exactly size bytes of a fixed-width value; a short read is a malformed payload.
        private ReadOnlySpan<byte> ReadExact(int size)
        {
            if (size > Remaining)
            {
                throw InvalidFile();
            }

            var span = _data.AsSpan(_pos, size);
            _pos += size;
            return span;
        }

        // Up to size bytes, short at the end of the payload: fewer bytes simply make a smaller number.
        private ReadOnlySpan<byte> ReadLoose(int size)
        {
            var count = Math.Min(size, Remaining);
            var span = _data.AsSpan(_pos, count);
            _pos += count;
            return span;
        }

        private byte ReadByte() => ReadExact(1)[0];

        // n big-endian unsigned integers of the given width; a width of 0 is malformed, and a value wider than 8 bytes
        // saturates to ulong.MaxValue, which no offset or reference can validly reach.
        private ulong[] ReadInts(ulong count, int size)
        {
            var total = size == 0 ? 0 : count > (ulong)Remaining / (ulong)size ? ulong.MaxValue : count * (ulong)size;
            var bytes = Read(total);
            if (size == 0)
            {
                throw InvalidFile();
            }

            var values = new ulong[count];
            for (var index = 0; index < values.Length; index++)
            {
                values[index] = ReadUnsigned(bytes.Slice(index * size, size));
            }

            return values;
        }

        private ulong[] ReadRefs(ulong count) => ReadInts(count, _refSize);

        private static ulong ReadUnsigned(ReadOnlySpan<byte> bytes)
        {
            var overflow = bytes.Length - 8;
            for (var index = 0; index < overflow; index++)
            {
                if (bytes[index] != 0)
                {
                    return ulong.MaxValue;
                }
            }

            ulong value = 0;
            for (var index = Math.Max(overflow, 0); index < bytes.Length; index++)
            {
                value = (value << 8) | bytes[index];
            }

            return value;
        }

        // The element count of a sized token: the low nibble, or for 0xF an integer whose byte count is 1 << (token & 3).
        private ulong GetSize(int tokenL)
        {
            if (tokenL != 0xF)
            {
                return (ulong)tokenL;
            }

            var width = 1 << (ReadByte() & 0x3);
            return ReadUnsigned(ReadExact(width));
        }

        // The object a reference names, read once and then served from the cache.
        private object? ReadObject(ulong reference, int depth)
        {
            if (depth > MaxNestingDepth)
            {
                throw TooDeep();
            }

            if (reference >= (ulong)_objects.Length)
            {
                throw InvalidFile();
            }

            var cached = _objects[reference];
            if (!ReferenceEquals(cached, Undefined))
            {
                return cached;
            }

            Seek(_offsets[reference]);
            var result = ReadToken(reference, depth);
            _objects[reference] = result;
            return result;
        }

        private object? ReadToken(ulong reference, int depth)
        {
            var token = ReadByte();
            var tokenH = token & 0xF0;
            var tokenL = token & 0x0F;
            switch (token)
            {
                case 0x00:
                    return null;
                case 0x08:
                    return false;
                case 0x09:
                    return true;
                case 0x0F:
                    return Array.Empty<byte>();
                case 0x22:
                    return (double)BinaryPrimitives.ReadSingleBigEndian(ReadExact(4));
                case 0x23:
                    return BinaryPrimitives.ReadDoubleBigEndian(ReadExact(8));
                case 0x33:
                    return ReadDate();
                default:
                    break;
            }

            return tokenH switch
            {
                0x10 => new BigInteger(ReadLoose(1 << tokenL), isUnsigned: tokenL < 3, isBigEndian: true),
                0x40 => Read(GetSize(tokenL)).ToArray(),
                0x50 => ReadAscii(GetSize(tokenL)),
                0x60 => ReadUtf16(GetSize(tokenL)),
                0x80 => ReadUid(tokenL),
                0xA0 => ReadArray(reference, GetSize(tokenL), depth),
                0xD0 => ReadDict(reference, GetSize(tokenL), depth),
                _ => throw InvalidFile(),
            };
        }

        // Seconds from the plist epoch: NaN and infinities are malformed; the integer part is exact, the fraction rounds to whole
        // microseconds half to even; the result must fall in years 1-9999.
        private DateTime ReadDate()
        {
            var seconds = BinaryPrimitives.ReadDoubleBigEndian(ReadExact(8));
            if (!double.IsFinite(seconds) || Math.Abs(seconds) >= 1e12)
            {
                throw InvalidFile();
            }

            var integerPart = Math.Truncate(seconds);
            var fractionMicroseconds = (seconds - integerPart) * 1e6;
            var microseconds = ((long)integerPart * 1_000_000) + (long)Math.Round(fractionMicroseconds, MidpointRounding.ToEven);
            if (microseconds < MinDateMicroseconds || microseconds >= MaxDateMicrosecondsExclusive)
            {
                throw InvalidFile();
            }

            return PlistEpoch.AddTicks(microseconds * 10);
        }

        private string ReadAscii(ulong size)
        {
            var bytes = Read(size);
            foreach (var value in bytes)
            {
                if (value > 0x7F)
                {
                    throw InvalidFile();
                }
            }

            return Encoding.ASCII.GetString(bytes);
        }

        // A sized run of UTF-16BE code units; a doubled size past any payload is a short read.
        private string ReadUtf16(ulong codeUnits)
        {
            var bytes = Read(codeUnits > ulong.MaxValue / 2 ? ulong.MaxValue : codeUnits * 2);
            try
            {
                return StrictUtf16Be.GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                throw InvalidFile();
            }
        }

        // A UID value, which has to fit in 64 bits.
        private Uid ReadUid(int tokenL)
        {
            var value = new BigInteger(ReadLoose(1 + tokenL), isUnsigned: true, isBigEndian: true);
            if (value >= UidLimit)
            {
                throw InvalidFile();
            }

            return new Uid(value);
        }

        private List<object?> ReadArray(ulong reference, ulong count, int depth)
        {
            var refs = ReadRefs(count);
            var result = new List<object?>(refs.Length);
            _objects[reference] = result;
            foreach (var item in refs)
            {
                result.Add(ReadObject(item, depth + 1));
            }

            return result;
        }

        private OrderedDictionary<string, object?> ReadDict(ulong reference, ulong count, int depth)
        {
            var keyRefs = ReadRefs(count);
            var valueRefs = ReadRefs(count);
            var result = new OrderedDictionary<string, object?>(StringComparer.Ordinal);
            _objects[reference] = result;
            for (var index = 0; index < keyRefs.Length; index++)
            {
                var value = ReadObject(valueRefs[index], depth + 1);
                result[ReadObject(keyRefs[index], depth + 1) as string ?? throw InvalidFile()] = value;
            }

            return result;
        }
    }
}
