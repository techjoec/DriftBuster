using System.Globalization;
using System.Text;

namespace DriftBuster.Backend.Infrastructure;

/// <summary><c>bytes.decode("utf-8")</c> (strict, final) with CPython 3.13's <c>UnicodeDecodeError</c> text for the first bad sequence.</summary>
/// <remarks>Derived from the publicly documented behaviour of CPython's UTF-8 codec, not its source text.</remarks>
public static class PythonUtf8
{
    private static readonly UTF8Encoding Strict = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// The decoded text; a byte sequence that is not UTF-8 raises <see cref="InvalidDataException"/> whose message is
    /// <c>str(UnicodeDecodeError)</c>, for example <c>'utf-8' codec can't decode byte 0xff in position 1: invalid start byte</c>.
    /// </summary>
    public static string Decode(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        try
        {
            return Strict.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            throw new InvalidDataException(DecodeErrorMessage(bytes));
        }
    }

    /// <summary>
    /// True when <paramref name="text"/> holds a surrogate that is not half of a pair: text strict UTF-8 cannot encode, and that
    /// the runtime's encoders silently replace with U+FFFD.
    /// </summary>
    public static bool HasUnpairedSurrogate(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        for (var index = 0; index < text.Length; index++)
        {
            if (char.IsHighSurrogate(text[index]) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
            {
                index++;
            }
            else if (char.IsSurrogate(text[index]))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary><c>str(UnicodeDecodeError)</c> for the first invalid sequence of <paramref name="bytes"/>; null when they are valid UTF-8.</summary>
    internal static string? DecodeErrorMessage(ReadOnlySpan<byte> bytes)
    {
        var position = 0;
        while (position < bytes.Length)
        {
            var (length, span, reason) = Sequence(bytes[position..]);
            if (reason is not null)
            {
                return span == 1
                    ? string.Create(CultureInfo.InvariantCulture, $"'utf-8' codec can't decode byte 0x{bytes[position]:x2} in position {position}: {reason}")
                    : string.Create(CultureInfo.InvariantCulture, $"'utf-8' codec can't decode bytes in position {position}-{position + span - 1}: {reason}");
            }

            position += length;
        }

        return null;
    }

    // One sequence at the start of rest: (bytes consumed, error span, error reason or null), following the checks of CPython's
    // decoder: the lead byte decides the length, the second byte's range is narrowed after E0, ED, F0 and F4, and a sequence cut
    // short by the end of the data reports every remaining byte unless a byte already present is invalid.
    private static (int Length, int Span, string? Reason) Sequence(ReadOnlySpan<byte> rest)
    {
        const string InvalidStart = "invalid start byte";
        const string InvalidContinuation = "invalid continuation byte";
        const string EndOfData = "unexpected end of data";
        var lead = rest[0];
        if (lead < 0x80)
        {
            return (1, 0, null);
        }

        if (lead < 0xC2 || lead > 0xF4)
        {
            return (1, 1, InvalidStart);
        }

        var length = lead < 0xE0 ? 2 : lead < 0xF0 ? 3 : 4;
        var (low, high) = lead switch
        {
            0xE0 => (0xA0, 0xBF),
            0xED => (0x80, 0x9F),
            0xF0 => (0x90, 0xBF),
            0xF4 => (0x80, 0x8F),
            _ => (0x80, 0xBF),
        };
        for (var index = 1; index < length; index++)
        {
            if (index >= rest.Length)
            {
                return (rest.Length, rest.Length, EndOfData);
            }

            var (min, max) = index == 1 ? (low, high) : (0x80, 0xBF);
            if (rest[index] < min || rest[index] > max)
            {
                return (index, index, InvalidContinuation);
            }
        }

        return (length, 0, null);
    }
}
