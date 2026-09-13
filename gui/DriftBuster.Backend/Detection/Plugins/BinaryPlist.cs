using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace DriftBuster.Backend.Detection.Plugins;

/// <summary>
/// Reader for the <c>bplist00</c> container with the acceptance rules of <c>plistlib._BinaryPlistParser</c>: the
/// 32-byte trailer (offset size, reference size, object count, top object, offset table offset), the offset table,
/// and the object table with null, booleans, the empty data marker, integers, 32- and 64-bit reals, dates, data,
/// ASCII and UTF-16BE strings, UIDs, arrays and dicts. Objects are cached by reference exactly as plistlib caches
/// them, so a shared reference yields one object and a container that references itself terminates.
/// </summary>
/// <remarks>
/// <para>
/// Values are Python-shaped: null, <see cref="bool"/>, <see cref="BigInteger"/>, <see cref="double"/>,
/// <see cref="DateTime"/>, <see cref="byte"/>[], <see cref="string"/>, <see cref="Uid"/>, <see cref="List{T}"/> and
/// an <see cref="OrderedDictionary{TKey, TValue}"/> keyed with Python's equality (<c>True == 1 == 1.0</c>, a NaN
/// equal only to itself). Every failure plistlib wraps into <c>InvalidFileException("Invalid file")</c> (short
/// reads, unknown tokens, out-of-range references, undecodable strings, unhashable keys, a UID at or above 2**64,
/// a date outside years 1-9999) surfaces as a <see cref="DecodeException"/> with that type and message.
/// </para>
/// <para>
/// Python's <c>RecursionError</c> is reproduced by counting interpreter frames: every Python-level call the parser
/// makes (<c>_read_object</c>, including one that returns a cached object, <c>_get_size</c>, <c>_read_refs</c>,
/// <c>_read_ints</c>, <c>_read</c>, the generator <c>_read_ints</c> uses for odd integer widths, <c>UID.__init__</c>
/// and <c>InvalidFileException.__init__</c>) pushes a frame, and pushing frame 1001 raises (the default recursion
/// limit of 1000). The count starts from <see cref="ParseFrame"/>, the depth <c>parse</c> runs at when a script's
/// <c>main()</c> calls a function that calls <c>Detector.scan_file</c> (the parity dump); another caller's stack
/// moves the boundary by its own frame count.
/// </para>
/// </remarks>
internal static partial class BinaryPlist
{
    /// <summary><c>sys.getrecursionlimit()</c>: the deepest frame the interpreter will push.</summary>
    internal const int FrameLimit = 1000;

    /// <summary>
    /// Frames on the stack inside <c>_BinaryPlistParser.parse</c> under the parity dump: module, <c>main</c>,
    /// <c>cmd_detect</c>, <c>scan_file</c>, <c>detect</c>, <c>_detect_binary_plist</c>, <c>plistlib.load</c>, <c>parse</c>.
    /// </summary>
    internal const int ParseFrame = 8;

    /// <summary>A decode failure carrying the Python exception class name plistlib would raise.</summary>
    internal sealed class DecodeException(string pythonType, string message) : Exception(message)
    {
        public string PythonType { get; } = pythonType;
    }

    /// <summary><c>plistlib.UID</c>; its string form is the class's <c>repr</c>, <c>UID(7)</c>, which <c>str()</c> falls back to.</summary>
    internal sealed record Uid(BigInteger Data)
    {
        public override string ToString() => $"UID({Data.ToString(CultureInfo.InvariantCulture)})";
    }

    /// <summary>The key <c>None</c> inside a dict (the dictionary type cannot hold a null key).</summary>
    internal static object NoneKey { get; } = new();

    private static readonly Encoding StrictUtf16Be = new UnicodeEncoding(bigEndian: true, byteOrderMark: false, throwOnInvalidBytes: true);

    private static readonly BigInteger UidLimit = BigInteger.One << 64;

    // datetime(2001, 1, 1) + timedelta(seconds=f) stays inside years 1-9999 for exactly this range of microseconds.
    private const long MinDateMicroseconds = -63113904000L * 1_000_000;
    private const long MaxDateMicrosecondsExclusive = 252423993600L * 1_000_000;
    private static readonly DateTime PlistEpoch = new(2001, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);

    // A failure raised as a C exception (IndexError, struct.error, ValueError, OverflowError, UnicodeDecodeError) and
    // wrapped by parse(), whose own InvalidFileException() frame always fits.
    private static DecodeException InvalidFile() => new("InvalidFileException", "Invalid file");

    private static DecodeException RecursionError() => new("RecursionError", "maximum recursion depth exceeded");

    // An InvalidFileException constructed at a frame: its __init__ is a Python frame one deeper, which may not fit.
    private static DecodeException InvalidFileRaisedAt(int frame) => frame + 1 > FrameLimit ? RecursionError() : InvalidFile();

    // A Python-level call that pushes this frame number.
    private static void Push(int frame)
    {
        if (frame > FrameLimit)
        {
            throw RecursionError();
        }
    }

    /// <summary><c>plistlib.load</c> over a bplist00 payload.</summary>
    /// <exception cref="DecodeException">When plistlib would raise.</exception>
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
            // seek(-32, SEEK_END) on a shorter payload raises ValueError, which plistlib wraps like every other failure.
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
            _offsets = ReadInts(numObjects, offsetSize, ParseFrame + 1);
            _objects = new object?[_offsets.Length];
            Array.Fill(_objects, Undefined);
            return ReadObject(topObject, ParseFrame + 1);
        }

        // BytesIO.seek past the end succeeds and every read from there is empty.
        private void Seek(ulong offset) => _pos = offset >= (ulong)_data.Length ? _data.Length : (int)offset;

        // _read called at readFrame: exactly size bytes, or InvalidFileException raised inside it.
        private ReadOnlySpan<byte> Read(ulong size, int readFrame)
        {
            Push(readFrame);
            if (size > (ulong)Remaining)
            {
                throw InvalidFileRaisedAt(readFrame);
            }

            var span = _data.AsSpan(_pos, (int)size);
            _pos += (int)size;
            return span;
        }

        // fp.read(size) followed by a C conversion: a short read raises a C exception wrapped by parse().
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

        // fp.read(size): up to size bytes, short at the end of the payload.
        private ReadOnlySpan<byte> ReadLoose(int size)
        {
            var count = Math.Min(size, Remaining);
            var span = _data.AsSpan(_pos, count);
            _pos += count;
            return span;
        }

        // fp.read(1)[0]: an empty read raises IndexError.
        private byte ReadByte() => ReadExact(1)[0];

        // _read_ints called at frame: n big-endian unsigned integers of the given width, read through _read one frame
        // deeper; a width of 0 raises after the (empty) read, and a width outside 1, 2, 4 and 8 converts through a
        // generator frame. Widths above 8 saturate to ulong.MaxValue, which no offset or reference can validly reach.
        private ulong[] ReadInts(ulong count, int size, int frame)
        {
            Push(frame);
            var total = size == 0 ? 0 : count > (ulong)Remaining / (ulong)size ? ulong.MaxValue : count * (ulong)size;
            var bytes = Read(total, frame + 1);
            if (size == 0)
            {
                throw InvalidFileRaisedAt(frame);
            }

            if (size is not (1 or 2 or 4 or 8))
            {
                Push(frame + 1);
            }

            var values = new ulong[count];
            for (var index = 0; index < values.Length; index++)
            {
                values[index] = ReadUnsigned(bytes.Slice(index * size, size));
            }

            return values;
        }

        // _read_refs called at frame.
        private ulong[] ReadRefs(ulong count, int frame)
        {
            Push(frame);
            return ReadInts(count, _refSize, frame + 1);
        }

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

        // _get_size called at frame: the low nibble, or for 0xF an int object whose byte count is 1 << (token & 3).
        private ulong GetSize(int tokenL, int frame)
        {
            Push(frame);
            if (tokenL != 0xF)
            {
                return (ulong)tokenL;
            }

            var width = 1 << (ReadByte() & 0x3);
            return ReadUnsigned(ReadExact(width));
        }

        // _read_object called at frame.
        private object? ReadObject(ulong reference, int frame)
        {
            Push(frame);

            // self._objects[ref] past the end raises IndexError.
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
            var result = ReadToken(reference, frame);
            _objects[reference] = result;
            return result;
        }

        private object? ReadToken(ulong reference, int frame)
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
                // int.from_bytes over a possibly short read: fewer bytes simply make a smaller number.
                0x10 => new BigInteger(ReadLoose(1 << tokenL), isUnsigned: tokenL < 3, isBigEndian: true),
                0x40 => Read(GetSize(tokenL, frame + 1), frame + 1).ToArray(),
                0x50 => ReadAscii(GetSize(tokenL, frame + 1), frame + 1),
                0x60 => ReadUtf16(GetSize(tokenL, frame + 1), frame + 1),
                0x80 => ReadUid(tokenL, frame + 1),
                0xA0 => ReadArray(reference, GetSize(tokenL, frame + 1), frame),
                0xD0 => ReadDict(reference, GetSize(tokenL, frame + 1), frame),
                _ => throw InvalidFileRaisedAt(frame),
            };
        }

        // timedelta(seconds=f): NaN and infinities fail the int conversion; the integer part is converted exactly
        // and only the fraction goes through float arithmetic, rounded to whole microseconds half to even; the
        // datetime sum then has to land inside years 1-9999.
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

        // data.decode('ascii') after _read at readFrame.
        private string ReadAscii(ulong size, int readFrame)
        {
            var bytes = Read(size, readFrame);
            foreach (var value in bytes)
            {
                if (value > 0x7F)
                {
                    throw InvalidFile();
                }
            }

            return Encoding.ASCII.GetString(bytes);
        }

        // data.decode('utf-16be') after _read(size * 2) at readFrame; a doubled size past any payload is a short read.
        private string ReadUtf16(ulong codeUnits, int readFrame)
        {
            var bytes = Read(codeUnits > ulong.MaxValue / 2 ? ulong.MaxValue : codeUnits * 2, readFrame);
            try
            {
                return StrictUtf16Be.GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                throw InvalidFile();
            }
        }

        // UID(int.from_bytes(...)) with UID.__init__ at initFrame; its ValueError for 2**64 and above is wrapped by parse().
        private Uid ReadUid(int tokenL, int initFrame)
        {
            var value = new BigInteger(ReadLoose(1 + tokenL), isUnsigned: true, isBigEndian: true);
            Push(initFrame);
            if (value >= UidLimit)
            {
                throw InvalidFile();
            }

            return new Uid(value);
        }

        private List<object?> ReadArray(ulong reference, ulong count, int frame)
        {
            var refs = ReadRefs(count, frame + 1);
            var result = new List<object?>(refs.Length);
            _objects[reference] = result;
            foreach (var item in refs)
            {
                result.Add(ReadObject(item, frame + 1));
            }

            return result;
        }

        private OrderedDictionary<object, object?> ReadDict(ulong reference, ulong count, int frame)
        {
            var keyRefs = ReadRefs(count, frame + 1);
            var valueRefs = ReadRefs(count, frame + 1);
            var result = new OrderedDictionary<object, object?>(KeyComparer.Instance);
            _objects[reference] = result;
            for (var index = 0; index < keyRefs.Length; index++)
            {
                // result[read(k)] = read(o): Python evaluates the right-hand side before the subscript.
                var value = ReadObject(valueRefs[index], frame + 1);
                var key = ReadObject(keyRefs[index], frame + 1) ?? NoneKey;
                if (key is List<object?> or OrderedDictionary<object, object?>)
                {
                    // Unhashable key: the TypeError is caught in _read_object and re-raised as InvalidFileException. A dict
                    // that reached its keys pushed _read here, so UID.__hash__ and this __init__ frame always fit.
                    throw InvalidFileRaisedAt(frame);
                }

                result[key] = value;
            }

            return result;
        }
    }
}
