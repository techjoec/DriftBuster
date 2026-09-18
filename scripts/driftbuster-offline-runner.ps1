<#
.SYNOPSIS
  Portable offline collector for DriftBuster profiles.

.DESCRIPTION
  Runs a DriftBuster offline runner config (https://driftbuster.dev/offline-runner/config/v1): collects file and glob sources,
  registry scans and SQLite snapshots into a staging directory, scrubs secret candidates, writes the manifest and run log,
  packages the result as a zip and optionally encrypts it with a DPAPI/AES keyset.

  Runs on Windows PowerShell 5.1 with nothing to install and nothing beside it: the C# helpers inside this script are compiled
  at load by the .NET Framework compiler that ships with Windows, and SQLite is read through Windows' built-in winsqlite3.dll.
  PowerShell 7 runs it too (on Linux SQLite comes from libsqlite3.so.0).

  Relative source, output and keyset paths resolve against the config file's directory.

.PARAMETER ConfigPath
  The offline runner config (JSON).

.PARAMETER OutputDirectory
  Overrides runner.output_directory; relative to the current location.

.EXAMPLE
  PS> .\driftbuster-offline-runner.ps1 -ConfigPath .\config.json

.EXAMPLE
  PS> .\driftbuster-offline-runner.ps1 -ConfigPath .\config.json -OutputDirectory C:\Collections
#>
using namespace DriftBusterOfflineRunner

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ConfigPath,

    [Parameter()]
    [string]$OutputDirectory
)

# Compiles the C# helpers below once per session. Windows PowerShell 5.1 compiles them with the .NET Framework compiler
# that ships with Windows; PowerShell 7 with its own. Nothing is installed.

function Import-DbOfflineRunnerNative {
    [CmdletBinding()]
    param()

    if ('DriftBusterOfflineRunner.Engine' -as [type]) {
        return
    }

    $source = @'
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

// The value semantics the offline runner follows: truthiness, text rendering, number parsing, iteration, text helpers and the
// JSON reader and writer. Written in C# 5 so Windows PowerShell 5.1's Add-Type compiles it with the .NET Framework compiler.
// Written from public documentation and specifications, not from another project's source.

namespace DriftBusterOfflineRunner
{
    /// <summary>
    /// A runner error: <see cref="ErrorType"/> names the .NET exception type the backend raises for the same failure
    /// (InvalidDataException, FormatException, FileNotFoundException, ...), the message is the backend's message.
    /// </summary>
    public sealed class EngineException : Exception
    {
        public EngineException(string errorType, string message) : base(message)
        {
            ErrorType = errorType;
        }

        public EngineException(string errorType, string message, Exception inner) : base(message, inner)
        {
            ErrorType = errorType;
        }

        public string ErrorType { get; private set; }
    }

    /// <summary>Python builtins over the JSON value domain (null, bool, BigInteger, double, string, IDictionary, IList, byte[]).</summary>
    public static class Engine
    {
        public static object Unwrap(object value)
        {
            var wrapped = value as System.Management.Automation.PSObject;
            if (wrapped != null)
            {
                var inner = wrapped.BaseObject;
                if (inner is System.Management.Automation.PSCustomObject)
                {
                    return value;
                }

                return inner;
            }

            return value;
        }

        public static bool IsInt(object value)
        {
            value = Unwrap(value);
            return value is BigInteger || value is int || value is long || value is short || value is byte || value is sbyte
                || value is uint || value is ulong || value is ushort;
        }

        public static bool IsFloat(object value)
        {
            value = Unwrap(value);
            return value is double || value is float || value is decimal;
        }

        public static BigInteger ToBig(object value)
        {
            value = Unwrap(value);
            if (value is BigInteger)
            {
                return (BigInteger)value;
            }

            if (value is ulong)
            {
                return new BigInteger((ulong)value);
            }

            return new BigInteger(Convert.ToInt64(value, CultureInfo.InvariantCulture));
        }

        public static double ToDouble(object value)
        {
            return Convert.ToDouble(Unwrap(value), CultureInfo.InvariantCulture);
        }

        public static bool IsMapping(object value)
        {
            return Unwrap(value) is IDictionary;
        }

        public static bool IsList(object value)
        {
            value = Unwrap(value);
            return value is IList && !(value is byte[]);
        }

        /// <summary>isinstance(value, collections.abc.Sequence) for JSON values: a str or a list.</summary>
        public static bool IsSequence(object value)
        {
            value = Unwrap(value);
            return value is string || IsList(value);
        }

        public static string TypeName(object value)
        {
            value = Unwrap(value);
            if (value == null)
            {
                return "null";
            }

            if (value is bool)
            {
                return "boolean";
            }

            if (IsInt(value))
            {
                return "integer";
            }

            if (IsFloat(value))
            {
                return "number";
            }

            if (value is string)
            {
                return "string";
            }

            if (value is byte[])
            {
                return "byte array";
            }

            if (value is IDictionary)
            {
                return "object";
            }

            if (value is IList)
            {
                return "array";
            }

            return value.GetType().Name;
        }

        public static bool Truthy(object value)
        {
            value = Unwrap(value);
            if (value == null)
            {
                return false;
            }

            if (value is bool)
            {
                return (bool)value;
            }

            var text = value as string;
            if (text != null)
            {
                return text.Length > 0;
            }

            if (IsInt(value))
            {
                return !ToBig(value).IsZero;
            }

            if (IsFloat(value))
            {
                return ToDouble(value) != 0.0;
            }

            var collection = value as ICollection;
            if (collection != null)
            {
                return collection.Count > 0;
            }

            return true;
        }

        /// <summary><c>a or b</c>: the first operand when truthy, else the second.</summary>
        public static object Or(object first, object second)
        {
            return Truthy(first) ? first : second;
        }

        /// <summary><c>mapping.get(key, default)</c> on a dict; InvalidDataException for anything else.</summary>
        public static object Get(object mapping, string key, object fallback)
        {
            var dictionary = Unwrap(mapping) as IDictionary;
            if (dictionary == null)
            {
                throw new EngineException("InvalidDataException", "expected a JSON object, not '" + TypeName(mapping) + "'");
            }

            return dictionary.Contains(key) ? dictionary[key] : fallback;
        }

        public static bool Has(object mapping, string key)
        {
            var dictionary = Unwrap(mapping) as IDictionary;
            return dictionary != null && dictionary.Contains(key);
        }

        /// <summary>The items <c>for item in value</c> yields: a str's code points, a dict's keys, a list's items.</summary>
        public static List<object> Iterate(object value)
        {
            value = Unwrap(value);
            var result = new List<object>();
            var text = value as string;
            if (text != null)
            {
                foreach (var codePoint in EngineText.CodePoints(text))
                {
                    result.Add(codePoint);
                }

                return result;
            }

            var dictionary = value as IDictionary;
            if (dictionary != null)
            {
                foreach (var key in dictionary.Keys)
                {
                    result.Add(key);
                }

                return result;
            }

            var list = value as IList;
            if (list != null && !(value is byte[]))
            {
                foreach (var item in list)
                {
                    result.Add(Unwrap(item));
                }

                return result;
            }

            throw new EngineException("InvalidDataException", "A value of type '" + TypeName(value) + "' cannot be enumerated.");
        }

        public static string Str(object value)
        {
            value = Unwrap(value);
            var text = value as string;
            return text ?? Repr(value);
        }

        public static string Repr(object value)
        {
            value = Unwrap(value);
            if (value == null)
            {
                return "null";
            }

            if (value is bool)
            {
                return (bool)value ? "true" : "false";
            }

            if (IsInt(value))
            {
                return ToBig(value).ToString(CultureInfo.InvariantCulture);
            }

            if (IsFloat(value))
            {
                return EngineFloat.Repr(ToDouble(value));
            }

            var text = value as string;
            if (text != null)
            {
                return EngineText.Repr(text);
            }

            var bytes = value as byte[];
            if (bytes != null)
            {
                return EngineText.BytesRepr(bytes);
            }

            var dictionary = value as IDictionary;
            if (dictionary != null)
            {
                var builder = new StringBuilder("{");
                var first = true;
                foreach (DictionaryEntry entry in dictionary)
                {
                    if (!first)
                    {
                        builder.Append(", ");
                    }

                    first = false;
                    builder.Append(Repr(entry.Key)).Append(": ").Append(Repr(entry.Value));
                }

                return builder.Append('}').ToString();
            }

            var list = value as IList;
            if (list != null)
            {
                var builder = new StringBuilder("[");
                for (var index = 0; index < list.Count; index++)
                {
                    if (index > 0)
                    {
                        builder.Append(", ");
                    }

                    builder.Append(Repr(list[index]));
                }

                return builder.Append(']').ToString();
            }

            return value.ToString();
        }

        /// <summary><c>int(value)</c> for a JSON value.</summary>
        public static BigInteger Int(object value)
        {
            value = Unwrap(value);
            if (value is bool)
            {
                return (bool)value ? BigInteger.One : BigInteger.Zero;
            }

            if (IsInt(value))
            {
                return ToBig(value);
            }

            if (IsFloat(value))
            {
                var number = ToDouble(value);
                if (double.IsNaN(number))
                {
                    throw new EngineException("InvalidDataException", "NaN cannot be converted to an integer.");
                }

                if (double.IsInfinity(number))
                {
                    throw new EngineException("OverflowException", "Infinity cannot be converted to an integer.");
                }

                return new BigInteger(Math.Truncate(number));
            }

            var text = value as string;
            if (text != null)
            {
                BigInteger parsed;
                if (TryParseIntText(text, out parsed))
                {
                    return parsed;
                }

                throw new EngineException("FormatException", "The value " + EngineText.Repr(text) + " is not a valid integer.");
            }

            throw new EngineException("InvalidDataException", "A value of type '" + TypeName(value) + "' cannot be converted to an integer.");
        }

        private static bool TryParseIntText(string text, out BigInteger result)
        {
            result = BigInteger.Zero;
            var body = EngineText.Strip(text);
            var index = 0;
            var negative = false;
            if (index < body.Length && (body[index] == '+' || body[index] == '-'))
            {
                negative = body[index] == '-';
                index++;
            }

            var digits = new StringBuilder();
            var previousUnderscore = true;
            for (; index < body.Length; index++)
            {
                var ch = body[index];
                if (ch == '_')
                {
                    if (previousUnderscore)
                    {
                        return false;
                    }

                    previousUnderscore = true;
                    continue;
                }

                var digit = char.IsSurrogate(ch) ? -1 : CharUnicodeInfo.GetDecimalDigitValue(ch);
                if (digit < 0 || CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.DecimalDigitNumber)
                {
                    return false;
                }

                digits.Append((char)('0' + digit));
                previousUnderscore = false;
            }

            if (digits.Length == 0 || previousUnderscore)
            {
                return false;
            }

            result = BigInteger.Parse(digits.ToString(), CultureInfo.InvariantCulture);
            if (negative)
            {
                result = -result;
            }

            return true;
        }

        /// <summary><c>float(value)</c> for a JSON value.</summary>
        public static double Float(object value)
        {
            value = Unwrap(value);
            if (value is bool)
            {
                return (bool)value ? 1.0 : 0.0;
            }

            if (IsInt(value))
            {
                var big = ToBig(value);
                var converted = (double)big;
                if (double.IsInfinity(converted))
                {
                    throw new EngineException("OverflowException", "The integer is too large to convert to a floating-point number.");
                }

                return converted;
            }

            if (IsFloat(value))
            {
                return ToDouble(value);
            }

            var text = value as string;
            if (text != null)
            {
                double parsed;
                if (EngineFloat.TryParse(EngineText.Strip(text), out parsed))
                {
                    return parsed;
                }

                throw new EngineException("FormatException", "The value " + EngineText.Repr(text) + " is not a valid number.");
            }

            throw new EngineException("InvalidDataException", "A value of type '" + TypeName(value) + "' cannot be converted to a number.");
        }

        /// <summary><c>value &lt;= 0</c> for an int, float or bool; InvalidDataException for anything else.</summary>
        public static bool LessOrEqualZero(object value)
        {
            value = Unwrap(value);
            if (value is bool)
            {
                return !(bool)value;
            }

            if (IsInt(value))
            {
                return ToBig(value).Sign <= 0;
            }

            if (IsFloat(value))
            {
                return ToDouble(value) <= 0.0;
            }

            throw new EngineException("InvalidDataException", "A value of type '" + TypeName(value) + "' cannot be compared with a value of type 'integer'.");
        }
    }

    /// <summary>float.__repr__ and float() parsing.</summary>
    public static class EngineFloat
    {
        public static string Repr(double value)
        {
            if (double.IsNaN(value))
            {
                return "nan";
            }

            if (double.IsPositiveInfinity(value))
            {
                return "inf";
            }

            if (double.IsNegativeInfinity(value))
            {
                return "-inf";
            }

            var negative = value < 0 || (value == 0.0 && BitConverter.DoubleToInt64Bits(value) < 0);
            var magnitude = Math.Abs(value);
            string digits;
            int decimalPoint;
            if (magnitude == 0.0)
            {
                digits = "0";
                decimalPoint = 1;
            }
            else
            {
                ShortestDigits(magnitude, out digits, out decimalPoint);
            }

            string body;
            if (decimalPoint <= -4 || decimalPoint > 16)
            {
                var exponent = decimalPoint - 1;
                body = digits.Substring(0, 1) + (digits.Length > 1 ? "." + digits.Substring(1) : string.Empty)
                    + "e" + (exponent < 0 ? "-" : "+") + Math.Abs(exponent).ToString("00", CultureInfo.InvariantCulture);
            }
            else if (decimalPoint <= 0)
            {
                body = "0." + new string('0', -decimalPoint) + digits;
            }
            else if (decimalPoint >= digits.Length)
            {
                body = digits + new string('0', decimalPoint - digits.Length) + ".0";
            }
            else
            {
                body = digits.Substring(0, decimalPoint) + "." + digits.Substring(decimalPoint);
            }

            return negative ? "-" + body : body;
        }

        // The shortest decimal digits that read back as value (round half to even), and the decimal point position
        // (value = 0.digits * 10^decimalPoint). Exact rational arithmetic, so the result does not depend on the runtime's formatter.
        private static void ShortestDigits(double value, out string digits, out int decimalPoint)
        {
            var bits = BitConverter.DoubleToInt64Bits(value);
            var exponentBits = (int)((bits >> 52) & 0x7FF);
            var fraction = bits & 0xFFFFFFFFFFFFFL;
            long mantissa;
            int exponent;
            if (exponentBits == 0)
            {
                mantissa = fraction;
                exponent = -1074;
            }
            else
            {
                mantissa = fraction | (1L << 52);
                exponent = exponentBits - 1075;
            }

            var lowerGapHalved = fraction == 0 && exponentBits > 1;
            // value, low and high bounds times 4, all scaled by 2^exponent.
            var scaledValue = new BigInteger(mantissa) * 4;
            var scaledLow = scaledValue - (lowerGapHalved ? 1 : 2);
            var scaledHigh = scaledValue + 2;
            var inclusive = (mantissa & 1) == 0;
            BigInteger numeratorScale = BigInteger.One;
            BigInteger denominator = BigInteger.One;
            if (exponent >= 0)
            {
                numeratorScale = BigInteger.Pow(2, exponent);
            }
            else
            {
                denominator = BigInteger.Pow(2, -exponent);
            }

            // Exact value as valueNum / valueDen.
            var valueNum = scaledValue * numeratorScale;
            var lowNum = scaledLow * numeratorScale;
            var highNum = scaledHigh * numeratorScale;
            var boundDen = denominator * 4;

            var k = (int)Math.Floor(Math.Log10(value));
            // Fix k so that 10^k <= value < 10^(k+1).
            while (Compare(Pow10Num(k), Pow10Den(k), valueNum, boundDen) > 0)
            {
                k--;
            }

            while (Compare(Pow10Num(k + 1), Pow10Den(k + 1), valueNum, boundDen) <= 0)
            {
                k++;
            }

            for (var count = 1; count <= 17; count++)
            {
                var power = k - count + 1;
                // scaled = value / 10^power = valueNum * Pow10Den(power) / (boundDen * Pow10Num(power))
                var num = valueNum * Pow10Den(power);
                var den = boundDen * Pow10Num(power);
                var floor = BigInteger.Divide(num, den);
                BigInteger best = BigInteger.MinusOne;
                BigInteger bestDistanceNum = BigInteger.Zero;
                for (var offset = 0; offset <= 1; offset++)
                {
                    var candidate = floor + offset;
                    // candidate * 10^power as candNum / candDen
                    var candNum = candidate * Pow10Num(power);
                    var candDen = Pow10Den(power);
                    var aboveLow = Compare(candNum, candDen, lowNum, boundDen);
                    var belowHigh = Compare(candNum, candDen, highNum, boundDen);
                    var inside = (aboveLow > 0 || (inclusive && aboveLow == 0)) && (belowHigh < 0 || (inclusive && belowHigh == 0));
                    if (!inside || candidate.IsZero)
                    {
                        continue;
                    }

                    var distance = BigInteger.Abs(candNum * boundDen - valueNum * candDen);
                    if (best.Sign < 0 || distance < bestDistanceNum)
                    {
                        best = candidate;
                        bestDistanceNum = distance;
                    }
                }

                if (best.Sign > 0)
                {
                    var text = best.ToString(CultureInfo.InvariantCulture);
                    decimalPoint = power + text.Length;
                    digits = text.TrimEnd('0');
                    if (digits.Length == 0)
                    {
                        digits = "0";
                    }

                    return;
                }
            }

            var fallback = value.ToString("E16", CultureInfo.InvariantCulture);
            digits = fallback.Substring(0, 1) + fallback.Substring(2, 16).TrimEnd('0');
            decimalPoint = int.Parse(fallback.Substring(fallback.IndexOf('E') + 1), CultureInfo.InvariantCulture) + 1;
        }

        private static BigInteger Pow10Num(int power)
        {
            return power >= 0 ? BigInteger.Pow(10, power) : BigInteger.One;
        }

        private static BigInteger Pow10Den(int power)
        {
            return power >= 0 ? BigInteger.One : BigInteger.Pow(10, -power);
        }

        private static int Compare(BigInteger leftNum, BigInteger leftDen, BigInteger rightNum, BigInteger rightDen)
        {
            return BigInteger.Compare(leftNum * rightDen, rightNum * leftDen);
        }

        private static readonly System.Text.RegularExpressions.Regex FloatGrammar = new System.Text.RegularExpressions.Regex(
            @"\A[+-]?(?:[0-9](?:_?[0-9])*(?:\.(?:[0-9](?:_?[0-9])*)?)?|\.[0-9](?:_?[0-9])*)(?:[eE][+-]?[0-9](?:_?[0-9])*)?\z",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        /// <summary>float(text) after stripping: decimal literals with single underscores between digits, inf, infinity and nan.</summary>
        public static bool TryParse(string text, out double result)
        {
            result = 0.0;
            var ascii = new StringBuilder(text.Length);
            foreach (var ch in text)
            {
                var digit = char.IsSurrogate(ch) || CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.DecimalDigitNumber
                    ? -1
                    : CharUnicodeInfo.GetDecimalDigitValue(ch);
                ascii.Append(digit >= 0 ? (char)('0' + digit) : ch);
            }

            var normalised = ascii.ToString();
            var unsigned = normalised.TrimStart('+', '-');
            var negative = normalised.StartsWith("-", StringComparison.Ordinal);
            if (unsigned.Length == normalised.Length - 1 || unsigned.Length == normalised.Length)
            {
                var word = unsigned.ToLowerInvariant();
                if (word == "inf" || word == "infinity")
                {
                    result = negative ? double.NegativeInfinity : double.PositiveInfinity;
                    return true;
                }

                if (word == "nan")
                {
                    result = double.NaN;
                    return true;
                }
            }

            if (!FloatGrammar.IsMatch(normalised))
            {
                return false;
            }

            return ParseDecimal(normalised.Replace("_", string.Empty), out result);
        }

        /// <summary>
        /// A decimal literal ([sign] digits [. digits] [e [sign] digits]) as the nearest double, ties to even, computed exactly: the .NET
        /// Framework parser does not always round correctly, and a sign on zero must survive.
        /// </summary>
        internal static bool ParseDecimal(string text, out double result)
        {
            result = 0.0;
            var index = 0;
            var negative = false;
            if (index < text.Length && (text[index] == '+' || text[index] == '-'))
            {
                negative = text[index] == '-';
                index++;
            }

            var digits = new StringBuilder();
            var fractionDigits = 0;
            var sawDot = false;
            for (; index < text.Length; index++)
            {
                var ch = text[index];
                if (ch >= '0' && ch <= '9')
                {
                    digits.Append(ch);
                    if (sawDot)
                    {
                        fractionDigits++;
                    }
                }
                else if (ch == '.' && !sawDot)
                {
                    sawDot = true;
                }
                else
                {
                    break;
                }
            }

            if (digits.Length == 0)
            {
                return false;
            }

            BigInteger exponent = BigInteger.Zero;
            if (index < text.Length)
            {
                if (text[index] != 'e' && text[index] != 'E')
                {
                    return false;
                }

                index++;
                var exponentNegative = false;
                if (index < text.Length && (text[index] == '+' || text[index] == '-'))
                {
                    exponentNegative = text[index] == '-';
                    index++;
                }

                if (index >= text.Length)
                {
                    return false;
                }

                for (; index < text.Length; index++)
                {
                    if (text[index] < '0' || text[index] > '9')
                    {
                        return false;
                    }

                    exponent = exponent * 10 + (text[index] - '0');
                }

                if (exponentNegative)
                {
                    exponent = -exponent;
                }
            }

            var mantissa = BigInteger.Parse(digits.ToString(), CultureInfo.InvariantCulture);
            exponent -= fractionDigits;
            result = Nearest(mantissa, exponent, digits.Length);
            if (negative)
            {
                result = -result;
                if (result == 0.0)
                {
                    result = BitConverter.Int64BitsToDouble(unchecked((long)0x8000000000000000UL));
                }
            }

            return true;
        }

        // The double nearest mantissa * 10^exponent (ties to even).
        private static double Nearest(BigInteger mantissa, BigInteger exponent, int digitCount)
        {
            if (mantissa.IsZero || exponent < -400 - digitCount)
            {
                return 0.0;
            }

            if (exponent > 400)
            {
                return double.PositiveInfinity;
            }

            var power = (int)exponent;
            var num = mantissa;
            var den = BigInteger.One;
            if (power >= 0)
            {
                num *= BigInteger.Pow(10, power);
            }
            else
            {
                den = BigInteger.Pow(10, -power);
            }

            var shift = 53 - (BitLength(num) - BitLength(den));
            var limit = new BigInteger(1L << 53);
            var floor = new BigInteger(1L << 52);
            BigInteger quotient;
            BigInteger remainder;
            BigInteger divisor;
            while (true)
            {
                if (shift > 1074)
                {
                    shift = 1074;
                }

                divisor = shift >= 0 ? den : den << -shift;
                var dividend = shift >= 0 ? num << shift : num;
                quotient = BigInteger.DivRem(dividend, divisor, out remainder);
                if (quotient >= limit)
                {
                    shift--;
                    continue;
                }

                if (quotient < floor && shift < 1074)
                {
                    shift++;
                    continue;
                }

                break;
            }

            var comparison = BigInteger.Compare(remainder * 2, divisor);
            if (comparison > 0 || (comparison == 0 && !quotient.IsEven))
            {
                quotient += 1;
            }

            if (quotient == limit)
            {
                quotient = floor;
                shift--;
            }

            if (52 - shift > 1023)
            {
                return double.PositiveInfinity;
            }

            long bits;
            if (quotient >= floor)
            {
                bits = ((long)(52 - shift + 1023) << 52) | (long)(quotient - floor);
            }
            else
            {
                bits = (long)quotient;
            }

            return BitConverter.Int64BitsToDouble(bits);
        }

        private static int BitLength(BigInteger value)
        {
            var bytes = value.ToByteArray();
            var top = bytes.Length - 1;
            while (top > 0 && bytes[top] == 0)
            {
                top--;
            }

            var length = top * 8;
            var last = bytes[top];
            while (last != 0)
            {
                length++;
                last >>= 1;
            }

            return length;
        }
    }

    /// <summary>str methods and text helpers with Python's character classes.</summary>
    public static class EngineText
    {
        public static List<string> CodePoints(string text)
        {
            var result = new List<string>();
            for (var index = 0; index < text.Length; index++)
            {
                if (char.IsHighSurrogate(text[index]) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
                {
                    result.Add(text.Substring(index, 2));
                    index++;
                }
                else
                {
                    result.Add(text.Substring(index, 1));
                }
            }

            return result;
        }

        /// <summary>str.isspace for one UTF-16 unit (every Python whitespace character is in the BMP).</summary>
        public static bool IsSpace(char ch)
        {
            return (ch >= (char)0x09 && ch <= (char)0x0D) || (ch >= (char)0x1C && ch <= (char)0x20) || ch == (char)0x85 || ch == (char)0xA0 || ch == (char)0x1680
                || (ch >= (char)0x2000 && ch <= (char)0x200A) || ch == (char)0x2028 || ch == (char)0x2029 || ch == (char)0x202F || ch == (char)0x205F || ch == (char)0x3000;
        }

        public static string Strip(string text)
        {
            var start = 0;
            var end = text.Length;
            while (start < end && IsSpace(text[start]))
            {
                start++;
            }

            while (end > start && IsSpace(text[end - 1]))
            {
                end--;
            }

            return text.Substring(start, end - start);
        }

        public static string Lower(string text)
        {
            return text.ToLowerInvariant();
        }

        public static string Upper(string text)
        {
            return text.ToUpperInvariant();
        }

        /// <summary>str.isalnum for one code point: a letter (L*) or a number (Nd, Nl, No).</summary>
        public static bool IsAlnum(string codePoint)
        {
            switch (CharUnicodeInfo.GetUnicodeCategory(codePoint, 0))
            {
                case UnicodeCategory.UppercaseLetter:
                case UnicodeCategory.LowercaseLetter:
                case UnicodeCategory.TitlecaseLetter:
                case UnicodeCategory.ModifierLetter:
                case UnicodeCategory.OtherLetter:
                case UnicodeCategory.DecimalDigitNumber:
                case UnicodeCategory.LetterNumber:
                case UnicodeCategory.OtherNumber:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>offline_runner._safe_name: each code point kept when alphanumeric, "-" or "_", else "-"; empty gives "data".</summary>
        public static string SafeName(string text)
        {
            var builder = new StringBuilder();
            foreach (var codePoint in CodePoints(text))
            {
                if (codePoint == "-" || codePoint == "_" || IsAlnum(codePoint))
                {
                    builder.Append(codePoint);
                }
                else
                {
                    builder.Append('-');
                }
            }

            return builder.Length > 0 ? builder.ToString() : "data";
        }

        /// <summary>The non-empty parts of <c>re.split(r"[\s,;]+", text)</c>.</summary>
        public static List<string> SplitSpaceCommaSemicolon(string text)
        {
            var parts = new List<string>();
            var current = new StringBuilder();
            foreach (var ch in text)
            {
                if (ch == ',' || ch == ';' || IsSpace(ch))
                {
                    if (current.Length > 0)
                    {
                        parts.Add(current.ToString());
                        current.Clear();
                    }
                }
                else
                {
                    current.Append(ch);
                }
            }

            if (current.Length > 0)
            {
                parts.Add(current.ToString());
            }

            return parts;
        }

        public static int CodePointLength(string text)
        {
            var count = 0;
            for (var index = 0; index < text.Length; index++)
            {
                if (char.IsHighSurrogate(text[index]) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
                {
                    index++;
                }

                count++;
            }

            return count;
        }

        public static string CodePointPrefix(string text, int count)
        {
            var offset = 0;
            for (var taken = 0; taken < count && offset < text.Length; taken++)
            {
                offset += char.IsHighSurrogate(text[offset]) && offset + 1 < text.Length && char.IsLowSurrogate(text[offset + 1]) ? 2 : 1;
            }

            return text.Substring(0, offset);
        }

        /// <summary>Python's str ordering: code point by code point.</summary>
        public static int CompareCodePoints(string left, string right)
        {
            var i = 0;
            var j = 0;
            while (i < left.Length && j < right.Length)
            {
                var a = char.IsSurrogatePair(left, i) ? char.ConvertToUtf32(left, i) : left[i];
                var b = char.IsSurrogatePair(right, j) ? char.ConvertToUtf32(right, j) : right[j];
                if (a != b)
                {
                    return a < b ? -1 : 1;
                }

                i += a > 0xFFFF ? 2 : 1;
                j += b > 0xFFFF ? 2 : 1;
            }

            var leftDone = i >= left.Length;
            var rightDone = j >= right.Length;
            return leftDone && rightDone ? 0 : (leftDone ? -1 : 1);
        }

        public static int CountCodePointLess(string left, string right)
        {
            return CompareCodePoints(left, right);
        }

        private static bool IsPrintable(string text, int index)
        {
            switch (CharUnicodeInfo.GetUnicodeCategory(text, index))
            {
                case UnicodeCategory.Control:
                case UnicodeCategory.Format:
                case UnicodeCategory.Surrogate:
                case UnicodeCategory.PrivateUse:
                case UnicodeCategory.OtherNotAssigned:
                case UnicodeCategory.LineSeparator:
                case UnicodeCategory.ParagraphSeparator:
                case UnicodeCategory.SpaceSeparator:
                    return false;
                default:
                    return true;
            }
        }

        /// <summary>repr(str).</summary>
        public static string Repr(string text)
        {
            var quote = text.IndexOf('\'') >= 0 && text.IndexOf('"') < 0 ? '"' : '\'';
            var builder = new StringBuilder();
            builder.Append(quote);
            for (var index = 0; index < text.Length; index++)
            {
                var ch = text[index];
                if (ch == quote || ch == '\\')
                {
                    builder.Append('\\').Append(ch);
                }
                else if (ch == '\t')
                {
                    builder.Append("\\t");
                }
                else if (ch == '\n')
                {
                    builder.Append("\\n");
                }
                else if (ch == '\r')
                {
                    builder.Append("\\r");
                }
                else if (ch < ' ' || ch == '\x7f')
                {
                    builder.Append("\\x").Append(((int)ch).ToString("x2", CultureInfo.InvariantCulture));
                }
                else if (ch < '\x7f')
                {
                    builder.Append(ch);
                }
                else if (char.IsSurrogatePair(text, index))
                {
                    if (IsPrintable(text, index))
                    {
                        builder.Append(text, index, 2);
                    }
                    else
                    {
                        builder.Append("\\U").Append(char.ConvertToUtf32(text, index).ToString("x8", CultureInfo.InvariantCulture));
                    }

                    index++;
                }
                else if (IsPrintable(text, index))
                {
                    builder.Append(ch);
                }
                else if (ch <= '\xff')
                {
                    builder.Append("\\x").Append(((int)ch).ToString("x2", CultureInfo.InvariantCulture));
                }
                else
                {
                    builder.Append("\\u").Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                }
            }

            return builder.Append(quote).ToString();
        }

        /// <summary>repr(bytes).</summary>
        public static string BytesRepr(byte[] bytes)
        {
            var hasSingle = Array.IndexOf(bytes, (byte)'\'') >= 0;
            var hasDouble = Array.IndexOf(bytes, (byte)'"') >= 0;
            var quote = hasSingle && !hasDouble ? '"' : '\'';
            var builder = new StringBuilder("b");
            builder.Append(quote);
            foreach (var value in bytes)
            {
                if (value == quote || value == '\\')
                {
                    builder.Append('\\').Append((char)value);
                }
                else if (value == '\t')
                {
                    builder.Append("\\t");
                }
                else if (value == '\n')
                {
                    builder.Append("\\n");
                }
                else if (value == '\r')
                {
                    builder.Append("\\r");
                }
                else if (value < 0x20 || value >= 0x7f)
                {
                    builder.Append("\\x").Append(value.ToString("x2", CultureInfo.InvariantCulture));
                }
                else
                {
                    builder.Append((char)value);
                }
            }

            return builder.Append(quote).ToString();
        }

        public static string Sha256Hex(byte[] data)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                return Hex(sha.ComputeHash(data));
            }
        }

        public static string Hex(byte[] data)
        {
            var builder = new StringBuilder(data.Length * 2);
            foreach (var value in data)
            {
                builder.Append(value.ToString("x2", CultureInfo.InvariantCulture));
            }

            return builder.ToString();
        }
    }

    /// <summary>
    /// Python's UTF-8 codec: each maximal ill-formed subpart becomes one U+FFFD under errors="replace" (the .NET Framework decoder merges
    /// some of them), and a stateful form carries an incomplete sequence across chunks until the final one.
    /// </summary>
    public sealed class EngineUtf8Decoder
    {
        private readonly byte[] _pending = new byte[3];
        private int _pendingCount;
        private long _base;

        /// <summary>True once any ill-formed sequence was replaced.</summary>
        public bool Invalid { get; private set; }

        /// <summary>The error message strict decoding raises for the first ill-formed sequence, or null.</summary>
        public string ErrorMessage { get; private set; }

        /// <summary>bytes.decode("utf-8") under errors="strict": the text, or InvalidDataException.</summary>
        public static string DecodeStrict(byte[] bytes)
        {
            var decoder = new EngineUtf8Decoder();
            var builder = new StringBuilder(bytes.Length);
            decoder.Decode(bytes, 0, bytes.Length, true, builder);
            if (decoder.Invalid)
            {
                throw new EngineException("InvalidDataException", decoder.ErrorMessage);
            }

            return builder.ToString();
        }

        public static string Decode(byte[] bytes, out bool invalid)
        {
            var decoder = new EngineUtf8Decoder();
            var builder = new StringBuilder(bytes.Length);
            decoder.Decode(bytes, 0, bytes.Length, true, builder);
            invalid = decoder.Invalid;
            return builder.ToString();
        }

        /// <summary>bytes.decode("utf-8", errors="replace").</summary>
        public static string DecodeReplace(byte[] bytes)
        {
            bool invalid;
            return Decode(bytes, out invalid);
        }

        public void Decode(byte[] bytes, int offset, int count, bool final, StringBuilder output)
        {
            byte[] data = bytes;
            var start = offset;
            var end = offset + count;
            if (_pendingCount > 0)
            {
                data = new byte[_pendingCount + count];
                Buffer.BlockCopy(_pending, 0, data, 0, _pendingCount);
                Buffer.BlockCopy(bytes, offset, data, _pendingCount, count);
                start = 0;
                end = data.Length;
                _pendingCount = 0;
            }

            var index = start;
            while (index < end)
            {
                var lead = data[index];
                if (lead < 0x80)
                {
                    output.Append((char)lead);
                    index++;
                    continue;
                }

                int need;
                var low = 0x80;
                var high = 0xBF;
                int codePoint;
                if (lead >= 0xC2 && lead <= 0xDF)
                {
                    need = 1;
                    codePoint = lead & 0x1F;
                }
                else if (lead >= 0xE0 && lead <= 0xEF)
                {
                    need = 2;
                    codePoint = lead & 0x0F;
                    if (lead == 0xE0)
                    {
                        low = 0xA0;
                    }
                    else if (lead == 0xED)
                    {
                        high = 0x9F;
                    }
                }
                else if (lead >= 0xF0 && lead <= 0xF4)
                {
                    need = 3;
                    codePoint = lead & 0x07;
                    if (lead == 0xF0)
                    {
                        low = 0x90;
                    }
                    else if (lead == 0xF4)
                    {
                        high = 0x8F;
                    }
                }
                else
                {
                    Replace(output, data, index, index + 1, start, "invalid start byte");
                    index++;
                    continue;
                }

                var next = index + 1;
                var taken = 0;
                while (taken < need && next < end)
                {
                    var trail = data[next];
                    if (trail < low || trail > high)
                    {
                        break;
                    }

                    codePoint = (codePoint << 6) | (trail & 0x3F);
                    low = 0x80;
                    high = 0xBF;
                    next++;
                    taken++;
                }

                if (taken == need)
                {
                    if (codePoint > 0xFFFF)
                    {
                        output.Append(char.ConvertFromUtf32(codePoint));
                    }
                    else
                    {
                        output.Append((char)codePoint);
                    }
                }
                else if (next >= end && !final)
                {
                    _pendingCount = end - index;
                    Buffer.BlockCopy(data, index, _pending, 0, _pendingCount);
                    _base += index - start;
                    return;
                }
                else if (next >= end)
                {
                    Replace(output, data, index, end, start, "unexpected end of data");
                }
                else
                {
                    Replace(output, data, index, next, start, "invalid continuation byte");
                }

                index = next;
            }

            _base += end - start;
        }

        private void Replace(StringBuilder output, byte[] data, int from, int to, int start, string reason)
        {
            if (!Invalid)
            {
                var position = (_base + from - start).ToString(CultureInfo.InvariantCulture);
                string detail;
                if (reason == "invalid start byte")
                {
                    detail = "byte 0x" + data[from].ToString("X2", CultureInfo.InvariantCulture) + " at position " + position + " cannot start a character";
                }
                else if (reason == "invalid continuation byte")
                {
                    detail = "the sequence at position " + position + " has an invalid continuation byte";
                }
                else
                {
                    detail = "the data ends inside the sequence at position " + position;
                }

                ErrorMessage = "The data is not valid UTF-8: " + detail + ".";
            }

            Invalid = true;
            output.Append('\uFFFD');
        }
    }

    /// <summary>json.loads and json.dumps (ensure_ascii, allow_nan).</summary>
    public static class EngineJson
    {
        // A JSON file as Windows PowerShell 5.1 writes it (Set-Content -Encoding UTF8, Out-File) starts with a byte order mark.
        public static object LoadsFile(string path)
        {
            var text = EngineFile.ReadText(path);
            return Loads(text.Length > 0 && text[0] == (char)0xFEFF ? text.Substring(1) : text);
        }

        public static object Loads(string text)
        {
            if (text.Length > 0 && text[0] == (char)0xFEFF)
            {
                throw Error("the text starts with a byte order mark", text, 0);
            }

            var reader = new Reader(text, 0);
            var index = reader.SkipWhitespace(0);
            var value = reader.ParseValue(ref index);
            index = reader.SkipWhitespace(index);
            if (index != text.Length)
            {
                throw Error("unexpected text follows the value", text, index);
            }

            return value;
        }

        internal static EngineException Error(string message, string text, int position)
        {
            var line = 1;
            var lastNewline = -1;
            for (var index = 0; index < position && index < text.Length; index++)
            {
                if (text[index] == '\n')
                {
                    line++;
                    lastNewline = index;
                }
            }

            var column = position - lastNewline;
            return new EngineException(
                "InvalidDataException",
                string.Format(CultureInfo.InvariantCulture, "The file is not valid JSON: {0} at line {1}, position {2}.", message, line, column));
        }

        private sealed class Reader
        {
            // Nesting past this depth raises InvalidDataException instead of exhausting the thread's stack.
            private const int MaxDepth = 1000;

            private readonly string _text;
            private int _depth;

            public Reader(string text, int depth)
            {
                _text = text;
                _depth = depth;
            }

            public int SkipWhitespace(int index)
            {
                while (index < _text.Length && (_text[index] == ' ' || _text[index] == '\t' || _text[index] == '\n' || _text[index] == '\r'))
                {
                    index++;
                }

                return index;
            }

            public object ParseValue(ref int index)
            {
                if (index >= _text.Length)
                {
                    throw Error("a value was expected", _text, index);
                }

                var ch = _text[index];
                if (ch == '"')
                {
                    return ParseString(ref index);
                }

                if (ch == '{')
                {
                    return ParseObject(ref index);
                }

                if (ch == '[')
                {
                    return ParseArray(ref index);
                }

                if (Matches(index, "null"))
                {
                    index += 4;
                    return null;
                }

                if (Matches(index, "true"))
                {
                    index += 4;
                    return true;
                }

                if (Matches(index, "false"))
                {
                    index += 5;
                    return false;
                }

                if (Matches(index, "NaN"))
                {
                    index += 3;
                    return double.NaN;
                }

                if (Matches(index, "Infinity"))
                {
                    index += 8;
                    return double.PositiveInfinity;
                }

                if (Matches(index, "-Infinity"))
                {
                    index += 9;
                    return double.NegativeInfinity;
                }

                if (ch == '-' || (ch >= '0' && ch <= '9'))
                {
                    var number = ParseNumber(ref index);
                    if (number != null)
                    {
                        return number;
                    }
                }

                throw Error("a value was expected", _text, index);
            }

            private bool Matches(int index, string literal)
            {
                return string.CompareOrdinal(_text, index, literal, 0, literal.Length) == 0 && index + literal.Length <= _text.Length;
            }

            private object ParseNumber(ref int index)
            {
                var start = index;
                var position = index;
                if (_text[position] == '-')
                {
                    position++;
                }

                if (position >= _text.Length || _text[position] < '0' || _text[position] > '9')
                {
                    return null;
                }

                if (_text[position] == '0')
                {
                    position++;
                }
                else
                {
                    while (position < _text.Length && _text[position] >= '0' && _text[position] <= '9')
                    {
                        position++;
                    }
                }

                var isFloat = false;
                if (position + 1 < _text.Length && _text[position] == '.' && _text[position + 1] >= '0' && _text[position + 1] <= '9')
                {
                    isFloat = true;
                    position += 2;
                    while (position < _text.Length && _text[position] >= '0' && _text[position] <= '9')
                    {
                        position++;
                    }
                }

                if (position < _text.Length && (_text[position] == 'e' || _text[position] == 'E'))
                {
                    var exponentStart = position + 1;
                    if (exponentStart < _text.Length && (_text[exponentStart] == '+' || _text[exponentStart] == '-'))
                    {
                        exponentStart++;
                    }

                    if (exponentStart < _text.Length && _text[exponentStart] >= '0' && _text[exponentStart] <= '9')
                    {
                        isFloat = true;
                        position = exponentStart;
                        while (position < _text.Length && _text[position] >= '0' && _text[position] <= '9')
                        {
                            position++;
                        }
                    }
                }

                var literal = _text.Substring(start, position - start);
                index = position;
                if (isFloat)
                {
                    double parsed;
                    EngineFloat.ParseDecimal(literal, out parsed);
                    return parsed;
                }

                return BigInteger.Parse(literal, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            }

            private string ParseString(ref int index)
            {
                var begin = index;
                var position = index + 1;
                var builder = new StringBuilder();
                while (true)
                {
                    if (position >= _text.Length)
                    {
                        throw Error("a string is not terminated", _text, begin);
                    }

                    var ch = _text[position];
                    if (ch == '"')
                    {
                        index = position + 1;
                        return builder.ToString();
                    }

                    if (ch == '\\')
                    {
                        if (position + 1 >= _text.Length)
                        {
                            throw Error("a string is not terminated", _text, begin);
                        }

                        var escape = _text[position + 1];
                        switch (escape)
                        {
                            case '"':
                                builder.Append('"');
                                break;
                            case '\\':
                                builder.Append('\\');
                                break;
                            case '/':
                                builder.Append('/');
                                break;
                            case 'b':
                                builder.Append('\b');
                                break;
                            case 'f':
                                builder.Append('\f');
                                break;
                            case 'n':
                                builder.Append('\n');
                                break;
                            case 'r':
                                builder.Append('\r');
                                break;
                            case 't':
                                builder.Append('\t');
                                break;
                            case 'u':
                                var unit = ReadHex(position + 2, position);
                                position += 6;
                                if (unit >= 0xD800 && unit <= 0xDBFF && position + 1 < _text.Length && _text[position] == '\\' && _text[position + 1] == 'u')
                                {
                                    var low = ReadHexOrNegative(position + 2);
                                    if (low >= 0xDC00 && low <= 0xDFFF)
                                    {
                                        builder.Append((char)unit).Append((char)low);
                                        position += 6;
                                        continue;
                                    }
                                }

                                builder.Append((char)unit);
                                continue;
                            default:
                                throw Error("a string holds an invalid escape", _text, position);
                        }

                        position += 2;
                        continue;
                    }

                    if (ch < ' ')
                    {
                        throw Error("a string holds a control character", _text, position);
                    }

                    builder.Append(ch);
                    position++;
                }
            }

            private int ReadHexOrNegative(int start)
            {
                if (start + 4 > _text.Length)
                {
                    return -1;
                }

                var value = 0;
                for (var offset = 0; offset < 4; offset++)
                {
                    var digit = HexDigit(_text[start + offset]);
                    if (digit < 0)
                    {
                        return -1;
                    }

                    value = (value << 4) | digit;
                }

                return value;
            }

            private int ReadHex(int start, int escapeAt)
            {
                var value = ReadHexOrNegative(start);
                if (value < 0)
                {
                    throw Error("a string holds an invalid \\u escape", _text, escapeAt + 1);
                }

                return value;
            }

            private static int HexDigit(char ch)
            {
                if (ch >= '0' && ch <= '9')
                {
                    return ch - '0';
                }

                if (ch >= 'a' && ch <= 'f')
                {
                    return ch - 'a' + 10;
                }

                if (ch >= 'A' && ch <= 'F')
                {
                    return ch - 'A' + 10;
                }

                return -1;
            }

            private void Enter(string kind)
            {
                if (++_depth > MaxDepth)
                {
                    throw new EngineException(
                        "InvalidDataException",
                        "The JSON document is nested more than " + MaxDepth.ToString(CultureInfo.InvariantCulture) + " levels deep.");
                }
            }

            private OrderedDictionary ParseObject(ref int index)
            {
                Enter("object");
                try
                {
                    return ParseObjectBody(ref index);
                }
                finally
                {
                    _depth--;
                }
            }

            private List<object> ParseArray(ref int index)
            {
                Enter("array");
                try
                {
                    return ParseArrayBody(ref index);
                }
                finally
                {
                    _depth--;
                }
            }

            private OrderedDictionary ParseObjectBody(ref int index)
            {
                var result = new OrderedDictionary(StringComparer.Ordinal);
                var position = SkipWhitespace(index + 1);
                if (position < _text.Length && _text[position] == '}')
                {
                    index = position + 1;
                    return result;
                }

                while (true)
                {
                    if (position >= _text.Length || _text[position] != '"')
                    {
                        throw Error("a property name in double quotes was expected", _text, position);
                    }

                    var key = ParseString(ref position);
                    position = SkipWhitespace(position);
                    if (position >= _text.Length || _text[position] != ':')
                    {
                        throw Error("':' was expected", _text, position);
                    }

                    position = SkipWhitespace(position + 1);
                    var value = ParseValue(ref position);
                    result[key] = value;
                    position = SkipWhitespace(position);
                    if (position < _text.Length && _text[position] == '}')
                    {
                        index = position + 1;
                        return result;
                    }

                    if (position >= _text.Length || _text[position] != ',')
                    {
                        throw Error("',' was expected", _text, position);
                    }

                    var comma = position;
                    position = SkipWhitespace(position + 1);
                    if (position < _text.Length && _text[position] == '}')
                    {
                        throw Error("a trailing comma ends an object", _text, comma);
                    }
                }
            }

            private List<object> ParseArrayBody(ref int index)
            {
                var result = new List<object>();
                var position = SkipWhitespace(index + 1);
                if (position < _text.Length && _text[position] == ']')
                {
                    index = position + 1;
                    return result;
                }

                while (true)
                {
                    result.Add(ParseValue(ref position));
                    position = SkipWhitespace(position);
                    if (position < _text.Length && _text[position] == ']')
                    {
                        index = position + 1;
                        return result;
                    }

                    if (position >= _text.Length || _text[position] != ',')
                    {
                        throw Error("',' was expected", _text, position);
                    }

                    var comma = position;
                    position = SkipWhitespace(position + 1);
                    if (position < _text.Length && _text[position] == ']')
                    {
                        throw Error("a trailing comma ends an array", _text, comma);
                    }
                }
            }
        }

        /// <summary>json.dumps(value, indent=indent (null when negative), sort_keys=sortKeys), ASCII-escaped.</summary>
        public static string Dumps(object value, int indent, bool sortKeys)
        {
            return Dumps(value, indent, sortKeys, false);
        }

        /// <summary>As <see cref="Dumps(object,int,bool)"/>; <paramref name="defaultStr"/> dumps bytes as the string of their repr (default=str).</summary>
        public static string Dumps(object value, int indent, bool sortKeys, bool defaultStr)
        {
            var builder = new StringBuilder();
            Write(builder, value, indent, sortKeys, defaultStr, 0);
            return builder.ToString();
        }

        private static void Write(StringBuilder builder, object value, int indent, bool sortKeys, bool defaultStr, int level)
        {
            value = Engine.Unwrap(value);
            if (value == null)
            {
                builder.Append("null");
                return;
            }

            if (value is bool)
            {
                builder.Append((bool)value ? "true" : "false");
                return;
            }

            if (Engine.IsInt(value))
            {
                builder.Append(Engine.ToBig(value).ToString(CultureInfo.InvariantCulture));
                return;
            }

            if (Engine.IsFloat(value))
            {
                builder.Append(FloatText(Engine.ToDouble(value)));
                return;
            }

            var text = value as string;
            if (text != null)
            {
                WriteString(builder, text);
                return;
            }

            var bytes = value as byte[];
            if (bytes != null)
            {
                if (!defaultStr)
                {
                    throw new EngineException("NotSupportedException", "A value of type 'byte array' cannot be written as JSON.");
                }

                WriteString(builder, EngineText.BytesRepr(bytes));
                return;
            }

            var dictionary = value as IDictionary;
            if (dictionary != null)
            {
                if (dictionary.Count == 0)
                {
                    builder.Append("{}");
                    return;
                }

                var entries = new List<KeyValuePair<string, object>>();
                foreach (DictionaryEntry entry in dictionary)
                {
                    entries.Add(new KeyValuePair<string, object>(KeyText(entry.Key), entry.Value));
                }

                if (sortKeys)
                {
                    StableSort(entries);
                }

                builder.Append('{');
                var first = true;
                foreach (var entry in entries)
                {
                    if (!first)
                    {
                        builder.Append(indent >= 0 ? "," : ", ");
                    }

                    first = false;
                    NewLine(builder, indent, level + 1);
                    WriteString(builder, entry.Key);
                    builder.Append(": ");
                    Write(builder, entry.Value, indent, sortKeys, defaultStr, level + 1);
                }

                NewLine(builder, indent, level);
                builder.Append('}');
                return;
            }

            var list = value as IEnumerable;
            if (list != null)
            {
                var items = new List<object>();
                foreach (var item in list)
                {
                    items.Add(item);
                }

                if (items.Count == 0)
                {
                    builder.Append("[]");
                    return;
                }

                builder.Append('[');
                for (var index = 0; index < items.Count; index++)
                {
                    if (index > 0)
                    {
                        builder.Append(indent >= 0 ? "," : ", ");
                    }

                    NewLine(builder, indent, level + 1);
                    Write(builder, items[index], indent, sortKeys, defaultStr, level + 1);
                }

                NewLine(builder, indent, level);
                builder.Append(']');
                return;
            }

            throw new EngineException("NotSupportedException", "A value of type '" + value.GetType().Name + "' cannot be written as JSON.");
        }

        private static void StableSort(List<KeyValuePair<string, object>> entries)
        {
            for (var index = 1; index < entries.Count; index++)
            {
                var current = entries[index];
                var position = index - 1;
                while (position >= 0 && EngineText.CompareCodePoints(entries[position].Key, current.Key) > 0)
                {
                    entries[position + 1] = entries[position];
                    position--;
                }

                entries[position + 1] = current;
            }
        }

        private static string KeyText(object key)
        {
            key = Engine.Unwrap(key);
            var text = key as string;
            if (text != null)
            {
                return text;
            }

            if (key == null)
            {
                return "null";
            }

            if (key is bool)
            {
                return (bool)key ? "true" : "false";
            }

            if (Engine.IsInt(key))
            {
                return Engine.ToBig(key).ToString(CultureInfo.InvariantCulture);
            }

            if (Engine.IsFloat(key))
            {
                return FloatText(Engine.ToDouble(key));
            }

            throw new EngineException("NotSupportedException", "A key of type '" + key.GetType().Name + "' cannot be written as JSON.");
        }

        private static void NewLine(StringBuilder builder, int indent, int level)
        {
            if (indent < 0)
            {
                return;
            }

            builder.Append('\n').Append(' ', indent * level);
        }

        public static string FloatText(double value)
        {
            if (double.IsNaN(value))
            {
                return "NaN";
            }

            if (double.IsPositiveInfinity(value))
            {
                return "Infinity";
            }

            if (double.IsNegativeInfinity(value))
            {
                return "-Infinity";
            }

            return EngineFloat.Repr(value);
        }

        private static void WriteString(StringBuilder builder, string text)
        {
            builder.Append('"');
            foreach (var ch in text)
            {
                switch (ch)
                {
                    case '"':
                        builder.Append("\\\"");
                        break;
                    case '\\':
                        builder.Append("\\\\");
                        break;
                    case '\n':
                        builder.Append("\\n");
                        break;
                    case '\r':
                        builder.Append("\\r");
                        break;
                    case '\t':
                        builder.Append("\\t");
                        break;
                    case '\b':
                        builder.Append("\\b");
                        break;
                    case '\f':
                        builder.Append("\\f");
                        break;
                    default:
                        if (ch < ' ' || ch > '~')
                        {
                            builder.Append("\\u").Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            builder.Append(ch);
                        }

                        break;
                }
            }

            builder.Append('"');
        }
    }
}

// The path semantics the offline runner follows: environment-variable and home expansion, lexical path text, a recursive tree walk
// and the stat-level checks, under the host's rules (Windows paths on Windows, POSIX paths elsewhere). Globbing and exclusions are
// PowerShell functions.
// Written from public documentation and specifications, not from another project's source.

namespace DriftBusterOfflineRunner
{
    /// <summary>The process view the path helpers use: the flavour and the working directory relative paths resolve against.</summary>
    public static class EngineOs
    {
        private static string _cwd;

        public static bool Windows
        {
            get { return Environment.OSVersion.Platform != PlatformID.Unix && Environment.OSVersion.Platform != PlatformID.MacOSX; }
        }

        public static string Sep
        {
            get { return Windows ? "\\" : "/"; }
        }

        /// <summary>The working directory (PowerShell's location, which .NET's own current directory does not follow).</summary>
        public static string Cwd
        {
            get { return _cwd ?? Environment.CurrentDirectory; }
            set { _cwd = value; }
        }

        /// <summary>The path a file system call receives: the path joined under <see cref="Cwd"/> when relative.</summary>
        public static string Abs(string path)
        {
            if (path.Length == 0)
            {
                return Cwd;
            }

            if (Windows)
            {
                string drive;
                string root;
                string tail;
                EnginePath.SplitRoot(path, out drive, out root, out tail);
                if (drive.Length > 0 || root.Length > 0)
                {
                    return path;
                }

                return Cwd.TrimEnd('\\', '/') + "\\" + path;
            }

            return path.StartsWith("/", StringComparison.Ordinal) ? path : Cwd.TrimEnd('/') + "/" + path;
        }

        public static string Environ(string name)
        {
            if (name.Length == 0 || name.IndexOf('\0') >= 0 || (Windows && name.IndexOf('=') >= 0))
            {
                return null;
            }

            return Environment.GetEnvironmentVariable(name);
        }
    }

    /// <summary>os.path and pathlib.PurePath over strings.</summary>
    public static class EnginePath
    {
        public static void SplitRoot(string path, out string drive, out string root, out string tail)
        {
            if (!EngineOs.Windows)
            {
                if (!path.StartsWith("/", StringComparison.Ordinal))
                {
                    drive = string.Empty;
                    root = string.Empty;
                    tail = path;
                }
                else if (path.Length < 2 || path[1] != '/' || (path.Length > 2 && path[2] == '/'))
                {
                    drive = string.Empty;
                    root = "/";
                    tail = path.Substring(1);
                }
                else
                {
                    drive = string.Empty;
                    root = "//";
                    tail = path.Substring(2);
                }

                return;
            }

            var normalised = path.Replace('/', '\\');
            drive = string.Empty;
            root = string.Empty;
            tail = path;
            if (normalised.StartsWith("\\", StringComparison.Ordinal))
            {
                if (normalised.Length > 1 && normalised[1] == '\\')
                {
                    var start = normalised.Length >= 8 && normalised.Substring(0, 8).ToUpperInvariant() == "\\\\?\\UNC\\" ? 8 : 2;
                    var index = normalised.IndexOf('\\', start);
                    if (index < 0)
                    {
                        drive = path;
                        tail = string.Empty;
                        return;
                    }

                    var index2 = normalised.IndexOf('\\', index + 1);
                    if (index2 < 0)
                    {
                        drive = path;
                        tail = string.Empty;
                        return;
                    }

                    drive = path.Substring(0, index2);
                    root = path.Substring(index2, 1);
                    tail = path.Substring(index2 + 1);
                    return;
                }

                root = path.Substring(0, 1);
                tail = path.Substring(1);
                return;
            }

            if (normalised.Length > 1 && normalised[1] == ':')
            {
                if (normalised.Length > 2 && normalised[2] == '\\')
                {
                    drive = path.Substring(0, 2);
                    root = path.Substring(2, 1);
                    tail = path.Substring(3);
                    return;
                }

                drive = path.Substring(0, 2);
                tail = path.Substring(2);
            }
        }

        // pathlib's parse: drive, root and the parts after them, empty and "." parts dropped.
        private static void Parse(string path, out string drive, out string root, out List<string> parts)
        {
            parts = new List<string>();
            drive = string.Empty;
            root = string.Empty;
            if (path.Length == 0)
            {
                return;
            }

            var sep = EngineOs.Sep;
            if (EngineOs.Windows)
            {
                path = path.Replace('/', '\\');
            }

            string rest;
            SplitRoot(path, out drive, out root, out rest);
            if (root.Length == 0 && drive.StartsWith(sep, StringComparison.Ordinal) && !drive.EndsWith(sep, StringComparison.Ordinal))
            {
                var driveParts = drive.Split(sep[0]);
                if ((driveParts.Length == 4 && driveParts[2] != "?" && driveParts[2] != ".") || driveParts.Length == 6)
                {
                    root = sep;
                }
            }

            foreach (var part in rest.Split(sep[0]))
            {
                if (part.Length > 0 && part != ".")
                {
                    parts.Add(part);
                }
            }
        }

        /// <summary>str(Path(path)).</summary>
        public static string Normalise(string path)
        {
            string drive;
            string root;
            List<string> parts;
            Parse(path, out drive, out root, out parts);
            return Format(drive, root, parts);
        }

        private static string Format(string drive, string root, List<string> parts)
        {
            var sep = EngineOs.Sep;
            if (drive.Length > 0 || root.Length > 0)
            {
                return drive + root + string.Join(sep, parts.ToArray());
            }

            if (parts.Count == 0)
            {
                return ".";
            }

            if (EngineOs.Windows && parts[0].Length > 1 && parts[0][1] == ':')
            {
                return "." + sep + string.Join(sep, parts.ToArray());
            }

            return string.Join(sep, parts.ToArray());
        }

        /// <summary>os.path.join(first, second).</summary>
        public static string OsJoin(string first, string second)
        {
            if (!EngineOs.Windows)
            {
                if (second.StartsWith("/", StringComparison.Ordinal))
                {
                    return second;
                }

                if (first.Length == 0 || first.EndsWith("/", StringComparison.Ordinal))
                {
                    return first + second;
                }

                return first + "/" + second;
            }

            string resultDrive;
            string resultRoot;
            string resultPath;
            SplitRoot(first, out resultDrive, out resultRoot, out resultPath);
            string drive;
            string root;
            string rest;
            SplitRoot(second, out drive, out root, out rest);
            if (root.Length > 0)
            {
                if (drive.Length > 0 || resultDrive.Length == 0)
                {
                    resultDrive = drive;
                }

                resultRoot = root;
                resultPath = rest;
            }
            else
            {
                if (drive.Length > 0 && drive != resultDrive)
                {
                    if (drive.ToLowerInvariant() != resultDrive.ToLowerInvariant())
                    {
                        return JoinTail(drive, root, rest);
                    }

                    resultDrive = drive;
                }

                if (resultPath.Length > 0 && resultPath[resultPath.Length - 1] != '\\' && resultPath[resultPath.Length - 1] != '/')
                {
                    resultPath += "\\";
                }

                resultPath += rest;
            }

            return JoinTail(resultDrive, resultRoot, resultPath);
        }

        private static string JoinTail(string drive, string root, string path)
        {
            if (path.Length > 0 && root.Length == 0 && drive.Length > 0 && ":\\/".IndexOf(drive[drive.Length - 1]) < 0)
            {
                return drive + "\\" + path;
            }

            return drive + root + path;
        }

        /// <summary>str(Path(first) / second).</summary>
        public static string Join(string first, string second)
        {
            return Normalise(OsJoin(Normalise(first), second));
        }

        public static string Name(string path)
        {
            string drive;
            string root;
            List<string> parts;
            Parse(path, out drive, out root, out parts);
            return parts.Count == 0 ? string.Empty : parts[parts.Count - 1];
        }

        public static string Suffix(string path)
        {
            var name = Name(path);
            var index = name.LastIndexOf('.');
            return index > 0 && index < name.Length - 1 ? name.Substring(index) : string.Empty;
        }

        public static string Stem(string path)
        {
            var name = Name(path);
            var index = name.LastIndexOf('.');
            return index > 0 && index < name.Length - 1 ? name.Substring(0, index) : name;
        }

        public static string Parent(string path)
        {
            string drive;
            string root;
            List<string> parts;
            Parse(path, out drive, out root, out parts);
            if (parts.Count == 0)
            {
                return Format(drive, root, parts);
            }

            parts.RemoveAt(parts.Count - 1);
            return Format(drive, root, parts);
        }

        /// <summary>Path(path).is_absolute().</summary>
        public static bool IsAbsolute(string path)
        {
            if (!EngineOs.Windows)
            {
                return path.StartsWith("/", StringComparison.Ordinal);
            }

            var head = (path.Length > 3 ? path.Substring(0, 3) : path).Replace('/', '\\');
            return head.StartsWith("\\\\", StringComparison.Ordinal) || (head.Length >= 3 && head.Substring(1, 2) == ":\\");
        }

        public static string AsPosix(string path)
        {
            return EngineOs.Windows ? path.Replace('\\', '/') : path;
        }

        private static string NormCase(string text)
        {
            return EngineOs.Windows ? text.ToLowerInvariant() : text;
        }

        /// <summary>str(Path(path).relative_to(other)), or null where the path is not under the other.</summary>
        public static string RelativeTo(string path, string other)
        {
            string drive;
            string root;
            List<string> parts;
            Parse(path, out drive, out root, out parts);
            string otherDrive;
            string otherRoot;
            List<string> otherParts;
            Parse(other, out otherDrive, out otherRoot, out otherParts);
            if (NormCase(drive + root) != NormCase(otherDrive + otherRoot) || otherParts.Count > parts.Count)
            {
                return null;
            }

            for (var index = 0; index < otherParts.Count; index++)
            {
                if (NormCase(parts[index]) != NormCase(otherParts[index]))
                {
                    return null;
                }
            }

            return Format(string.Empty, string.Empty, parts.GetRange(otherParts.Count, parts.Count - otherParts.Count));
        }

        /// <summary>Path ordering: the case-normalised parts compared code point by code point.</summary>
        public static int CompareParts(string left, string right)
        {
            var leftParts = NormCase(left).Split(EngineOs.Sep[0]);
            var rightParts = NormCase(right).Split(EngineOs.Sep[0]);
            for (var index = 0; index < leftParts.Length && index < rightParts.Length; index++)
            {
                var compared = EngineText.CompareCodePoints(leftParts[index], rightParts[index]);
                if (compared != 0)
                {
                    return compared;
                }
            }

            return leftParts.Length.CompareTo(rightParts.Length);
        }

        public static void SortByParts(List<string> paths)
        {
            paths.Sort(CompareParts);
        }

        public static void SortByText(List<string> paths)
        {
            paths.Sort(EngineText.CompareCodePoints);
        }

        /// <summary>Path(path).with_suffix(suffix) as text.</summary>
        public static string WithSuffix(string path, string suffix)
        {
            var sep = EngineOs.Sep;
            if (suffix.IndexOf(sep, StringComparison.Ordinal) >= 0 || (EngineOs.Windows && suffix.IndexOf('/') >= 0)
                || (suffix.Length > 0 && (!suffix.StartsWith(".", StringComparison.Ordinal) || suffix == ".")))
            {
                throw new EngineException("ArgumentException", "Invalid suffix " + EngineText.Repr(suffix) + ".");
            }

            var name = Name(path);
            if (name.Length == 0)
            {
                throw new EngineException("ArgumentException", "The path " + EngineText.Repr(Normalise(path)) + " has an empty name.");
            }

            var stem = Stem(path);
            return Join(Parent(path), stem + suffix);
        }

        /// <summary>os.path.expandvars(path).</summary>
        public static string ExpandVars(string path)
        {
            return EngineOs.Windows ? ExpandVarsNt(path) : ExpandVarsPosix(path);
        }

        private static readonly Regex PosixVariable = new Regex(@"\$([A-Za-z0-9_]+|\{[^}]*\})", RegexOptions.CultureInvariant);

        private static string ExpandVarsPosix(string path)
        {
            if (path.IndexOf('$') < 0)
            {
                return path;
            }

            var index = 0;
            while (true)
            {
                var match = PosixVariable.Match(path, index);
                if (!match.Success)
                {
                    break;
                }

                var name = match.Groups[1].Value;
                if (name.StartsWith("{", StringComparison.Ordinal) && name.EndsWith("}", StringComparison.Ordinal))
                {
                    name = name.Substring(1, name.Length - 2);
                }

                var value = EngineOs.Environ(name);
                if (value == null)
                {
                    index = match.Index + match.Length;
                    continue;
                }

                var tail = path.Substring(match.Index + match.Length);
                path = path.Substring(0, match.Index) + value;
                index = path.Length;
                path += tail;
            }

            return path;
        }

        private static bool IsVarChar(char ch)
        {
            return (ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9') || ch == '_' || ch == '-';
        }

        private static string ExpandVarsNt(string path)
        {
            if (path.IndexOf('$') < 0 && path.IndexOf('%') < 0)
            {
                return path;
            }

            var result = new StringBuilder();
            var index = 0;
            var length = path.Length;
            while (index < length)
            {
                var ch = path[index];
                if (ch == '\'')
                {
                    path = path.Substring(index + 1);
                    length = path.Length;
                    var close = path.IndexOf('\'');
                    if (close >= 0)
                    {
                        index = close;
                        result.Append('\'').Append(path, 0, close + 1);
                    }
                    else
                    {
                        result.Append('\'').Append(path);
                        index = length - 1;
                    }
                }
                else if (ch == '%')
                {
                    if (index + 1 < length && path[index + 1] == '%')
                    {
                        result.Append('%');
                        index++;
                    }
                    else
                    {
                        path = path.Substring(index + 1);
                        length = path.Length;
                        var close = path.IndexOf('%');
                        if (close < 0)
                        {
                            result.Append('%').Append(path);
                            index = length - 1;
                        }
                        else
                        {
                            index = close;
                            var name = path.Substring(0, close);
                            var value = EngineOs.Environ(name);
                            result.Append(value ?? "%" + name + "%");
                        }
                    }
                }
                else if (ch == '$')
                {
                    if (index + 1 < length && path[index + 1] == '$')
                    {
                        result.Append('$');
                        index++;
                    }
                    else if (index + 1 < length && path[index + 1] == '{')
                    {
                        path = path.Substring(index + 2);
                        length = path.Length;
                        var close = path.IndexOf('}');
                        if (close < 0)
                        {
                            result.Append("${").Append(path);
                            index = length - 1;
                        }
                        else
                        {
                            index = close;
                            var name = path.Substring(0, close);
                            var value = EngineOs.Environ(name);
                            result.Append(value ?? "${" + name + "}");
                        }
                    }
                    else
                    {
                        var name = new StringBuilder();
                        index++;
                        while (index < length && IsVarChar(path[index]))
                        {
                            name.Append(path[index]);
                            index++;
                        }

                        var value = EngineOs.Environ(name.ToString());
                        result.Append(value ?? "$" + name);
                        if (index < length)
                        {
                            index--;
                        }
                    }
                }
                else
                {
                    result.Append(ch);
                }

                index++;
            }

            return result.ToString();
        }

        /// <summary>os.path.expanduser(path).</summary>
        public static string ExpandUser(string path)
        {
            if (!path.StartsWith("~", StringComparison.Ordinal))
            {
                return path;
            }

            if (EngineOs.Windows)
            {
                var end = 1;
                while (end < path.Length && path[end] != '\\' && path[end] != '/')
                {
                    end++;
                }

                string userHome;
                var profile = EngineOs.Environ("USERPROFILE");
                if (profile != null)
                {
                    userHome = profile;
                }
                else if (EngineOs.Environ("HOMEPATH") == null)
                {
                    return path;
                }
                else
                {
                    userHome = OsJoin(EngineOs.Environ("HOMEDRIVE") ?? string.Empty, EngineOs.Environ("HOMEPATH"));
                }

                if (end != 1)
                {
                    var target = path.Substring(1, end - 1);
                    var current = EngineOs.Environ("USERNAME");
                    if (target != current)
                    {
                        string headDrive;
                        string headRoot;
                        string headTail;
                        SplitRoot(userHome, out headDrive, out headRoot, out headTail);
                        var cut = headTail.Length;
                        while (cut > 0 && headTail[cut - 1] != '\\' && headTail[cut - 1] != '/')
                        {
                            cut--;
                        }

                        var baseName = headTail.Substring(cut);
                        if (current != baseName)
                        {
                            return path;
                        }

                        var dirName = headDrive + headRoot + headTail.Substring(0, cut).TrimEnd('\\', '/');
                        userHome = OsJoin(dirName, target);
                    }
                }

                return userHome + path.Substring(end);
            }

            var slash = path.IndexOf('/', 1);
            if (slash < 0)
            {
                slash = path.Length;
            }

            string home;
            if (slash == 1)
            {
                home = EngineOs.Environ("HOME") ?? PasswdHome(null);
                if (home == null)
                {
                    return path;
                }
            }
            else
            {
                home = PasswdHome(path.Substring(1, slash - 1));
                if (home == null)
                {
                    return path;
                }
            }

            home = home.TrimEnd('/');
            var expanded = home + path.Substring(slash);
            return expanded.Length > 0 ? expanded : "/";
        }

        // pwd.getpwnam(name).pw_dir from /etc/passwd (the uid's entry for null); null when not found.
        private static string PasswdHome(string name)
        {
            try
            {
                var userName = name ?? Environment.UserName;
                foreach (var line in File.ReadAllLines("/etc/passwd"))
                {
                    var fields = line.Split(':');
                    if (fields.Length >= 7 && fields[0] == userName)
                    {
                        return fields[5];
                    }
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            return null;
        }

        /// <summary>Path(path).expanduser() as text: InvalidOperationException when the home directory cannot be determined.</summary>
        public static string PathExpandUser(string path)
        {
            string drive;
            string root;
            List<string> parts;
            Parse(path, out drive, out root, out parts);
            if (drive.Length > 0 || root.Length > 0 || parts.Count == 0 || !parts[0].StartsWith("~", StringComparison.Ordinal))
            {
                return Format(drive, root, parts);
            }

            var home = ExpandUser(parts[0]);
            if (home.StartsWith("~", StringComparison.Ordinal))
            {
                throw new EngineException("InvalidOperationException", "Could not determine home directory.");
            }

            parts.RemoveAt(0);
            string homeDrive;
            string homeRoot;
            List<string> homeParts;
            Parse(home, out homeDrive, out homeRoot, out homeParts);
            homeParts.AddRange(parts);
            return Format(homeDrive, homeRoot, homeParts);
        }
    }

    /// <summary>File system checks with Python's link semantics.</summary>
    public static class EngineFs
    {
        private const uint ReparseTagSymlink = 0xA000000C;
        private const int FileAttributeDirectory = 0x10;
        private const int FileAttributeReparsePoint = 0x400;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct Win32FindData
        {
            public int FileAttributes;
            public uint CreationTimeLow;
            public uint CreationTimeHigh;
            public uint LastAccessTimeLow;
            public uint LastAccessTimeHigh;
            public uint LastWriteTimeLow;
            public uint LastWriteTimeHigh;
            public int FileSizeHigh;
            public int FileSizeLow;
            public uint Reserved0;
            public uint Reserved1;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string FileName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)]
            public string AlternateFileName;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ByHandleFileInformation
        {
            public int FileAttributes;
            public uint CreationTimeLow;
            public uint CreationTimeHigh;
            public uint LastAccessTimeLow;
            public uint LastAccessTimeHigh;
            public uint LastWriteTimeLow;
            public uint LastWriteTimeHigh;
            public int VolumeSerialNumber;
            public int FileSizeHigh;
            public int FileSizeLow;
            public int NumberOfLinks;
            public int FileIndexHigh;
            public int FileIndexLow;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr FindFirstFileW(string fileName, out Win32FindData data);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FindClose(IntPtr handle);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateFileW(string fileName, int access, int share, IntPtr security, int disposition, int flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandle(IntPtr handle, out ByHandleFileInformation information);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int GetFinalPathNameByHandleW(IntPtr handle, StringBuilder path, int length, int flags);

        private static readonly IntPtr InvalidHandle = new IntPtr(-1);

        private static readonly PropertyInfo LinkTargetProperty = typeof(FileSystemInfo).GetProperty("LinkTarget");

        [DllImport("libc", SetLastError = true)]
        private static extern int statx(int directory, byte[] path, int flags, uint mask, byte[] buffer);

        // stat(path).st_mode off Windows (links followed, through statx, whose layout is the same on every architecture); -1 when the
        // path does not resolve.
        private static int StatMode(string path)
        {
            var name = Encoding.UTF8.GetBytes(EngineOs.Abs(path) + "\0");
            var buffer = new byte[256];
            if (statx(-100, name, 0, 0x1, buffer) != 0)
            {
                return -1;
            }

            return buffer[28] | (buffer[29] << 8);
        }

        /// <summary>Path.is_symlink().</summary>
        public static bool IsSymlink(string path)
        {
            var target = EngineOs.Abs(path);
            if (EngineOs.Windows)
            {
                var trimmed = target.TrimEnd('\\', '/');
                if (trimmed.Length == 0 || trimmed.EndsWith(":", StringComparison.Ordinal))
                {
                    return false;
                }

                Win32FindData data;
                var handle = FindFirstFileW(trimmed, out data);
                if (handle == InvalidHandle)
                {
                    return false;
                }

                FindClose(handle);
                return (data.FileAttributes & FileAttributeReparsePoint) != 0 && data.Reserved0 == ReparseTagSymlink;
            }

            return ReadLink(target) != null;
        }

        private static string ReadLink(string path)
        {
            if (LinkTargetProperty == null)
            {
                return null;
            }

            try
            {
                return (string)LinkTargetProperty.GetValue(new FileInfo(path), null);
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
            catch (TargetInvocationException)
            {
                return null;
            }
        }

        // The attributes and size of the file the path names, links followed; false when it cannot be opened.
        private static bool WinStat(string path, out int attributes, out long size)
        {
            attributes = 0;
            size = 0;
            var handle = CreateFileW(EngineOs.Abs(path), 0x80, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
            if (handle == InvalidHandle)
            {
                return false;
            }

            try
            {
                ByHandleFileInformation information;
                if (!GetFileInformationByHandle(handle, out information))
                {
                    return false;
                }

                attributes = information.FileAttributes;
                size = ((long)(uint)information.FileSizeHigh << 32) | (uint)information.FileSizeLow;
                return true;
            }
            finally
            {
                CloseHandle(handle);
            }
        }

        /// <summary>os.path.realpath(path) (strict=False).</summary>
        public static string Realpath(string path)
        {
            var absolute = EngineOs.Abs(path);
            if (EngineOs.Windows)
            {
                var handle = CreateFileW(absolute, 0x80, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
                if (handle == InvalidHandle)
                {
                    return EnginePath.Normalise(absolute);
                }

                try
                {
                    var buffer = new StringBuilder(1024);
                    var length = GetFinalPathNameByHandleW(handle, buffer, buffer.Capacity, 0);
                    if (length > buffer.Capacity)
                    {
                        buffer = new StringBuilder(length + 1);
                        length = GetFinalPathNameByHandleW(handle, buffer, buffer.Capacity, 0);
                    }

                    if (length <= 0)
                    {
                        return EnginePath.Normalise(absolute);
                    }

                    var final = buffer.ToString(0, length);
                    if (final.StartsWith("\\\\?\\UNC\\", StringComparison.OrdinalIgnoreCase))
                    {
                        return "\\\\" + final.Substring(8);
                    }

                    return final.StartsWith("\\\\?\\", StringComparison.Ordinal) ? final.Substring(4) : final;
                }
                finally
                {
                    CloseHandle(handle);
                }
            }

            var pending = new Stack<string>();
            var parts = absolute.Split('/');
            for (var index = parts.Length - 1; index >= 0; index--)
            {
                pending.Push(parts[index]);
            }

            var resolved = "/";
            var expansions = 0;
            while (pending.Count > 0)
            {
                var name = pending.Pop();
                if (name.Length == 0 || name == ".")
                {
                    continue;
                }

                if (name == "..")
                {
                    var cut = resolved.LastIndexOf('/');
                    resolved = cut <= 0 ? "/" : resolved.Substring(0, cut);
                    continue;
                }

                var candidate = resolved == "/" ? "/" + name : resolved + "/" + name;
                var link = ReadLink(candidate);
                if (link == null || expansions > 40)
                {
                    resolved = candidate;
                    continue;
                }

                expansions++;
                if (link.StartsWith("/", StringComparison.Ordinal))
                {
                    resolved = "/";
                }

                var linkParts = link.Split('/');
                for (var index = linkParts.Length - 1; index >= 0; index--)
                {
                    pending.Push(linkParts[index]);
                }
            }

            return resolved;
        }

        /// <summary>os.path.lexists(path).</summary>
        public static bool LExists(string path)
        {
            var target = EngineOs.Abs(path);
            return File.Exists(target) || Directory.Exists(target) || IsSymlink(path);
        }

        /// <summary>Path.exists(): links followed.</summary>
        public static bool Exists(string path)
        {
            if (EngineOs.Windows)
            {
                if (!IsSymlink(path))
                {
                    var target = EngineOs.Abs(path);
                    return File.Exists(target) || Directory.Exists(target);
                }

                int attributes;
                long size;
                return WinStat(path, out attributes, out size);
            }

            return StatMode(path) >= 0;
        }

        /// <summary>Path.is_dir(): links followed.</summary>
        public static bool IsDir(string path)
        {
            if (EngineOs.Windows)
            {
                if (!IsSymlink(path))
                {
                    return Directory.Exists(EngineOs.Abs(path));
                }

                int attributes;
                long size;
                return WinStat(path, out attributes, out size) && (attributes & FileAttributeDirectory) != 0;
            }

            var mode = StatMode(path);
            return mode >= 0 && (mode & 0xF000) == 0x4000;
        }

        /// <summary>Path.is_file(): links followed.</summary>
        public static bool IsFile(string path)
        {
            if (EngineOs.Windows)
            {
                if (!IsSymlink(path))
                {
                    return File.Exists(EngineOs.Abs(path));
                }

                int attributes;
                long size;
                return WinStat(path, out attributes, out size) && (attributes & FileAttributeDirectory) == 0;
            }

            var mode = StatMode(path);
            return mode >= 0 && (mode & 0xF000) == 0x8000;
        }

        /// <summary>path.stat().st_size.</summary>
        public static long Size(string path)
        {
            if (EngineOs.Windows && IsSymlink(path))
            {
                int attributes;
                long size;
                if (!WinStat(path, out attributes, out size))
                {
                    throw new EngineException("FileNotFoundException", "Could not find file '" + path + "'.");
                }

                return size;
            }

            var real = EngineOs.Windows ? EngineOs.Abs(path) : Realpath(path);
            return new FileInfo(real).Length;
        }

        /// <summary>The entry names os.scandir lists; an empty list when the directory cannot be listed.</summary>
        public static List<string> ListDir(string path, bool directoriesOnly)
        {
            var names = new List<string>();
            var directory = EngineOs.Abs(path.Length == 0 ? "." : path);
            try
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
                {
                    var name = entry.Substring(entry.LastIndexOfAny(new[] { '/', '\\' }) + 1);
                    if (!directoriesOnly || IsDir(EnginePath.OsJoin(path.Length == 0 ? "." : path, name)))
                    {
                        names.Add(name);
                    }
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
            catch (System.Security.SecurityException)
            {
            }

            return names;
        }

        /// <summary>
        /// The files of Path(path).rglob("*") (every entry below the directory, symlinked directories listed but not entered, unreadable
        /// directories skipped) for which is_file() holds, each as str(path / relative).
        /// </summary>
        public static List<string> RglobFiles(string path)
        {
            var files = new List<string>();
            var pending = new Stack<string>();
            pending.Push(EnginePath.Normalise(path));
            while (pending.Count > 0)
            {
                var directory = pending.Pop();
                foreach (var name in ListDir(directory, false))
                {
                    var child = EnginePath.Join(directory, name);
                    var symlink = IsSymlink(child);
                    if (!symlink && Directory.Exists(EngineOs.Abs(child)))
                    {
                        pending.Push(child);
                        continue;
                    }

                    if (IsFile(child))
                    {
                        files.Add(child);
                    }
                }
            }

            return files;
        }
    }
}

// Secret rule compilation, detection context and the filtered copy, with a guard for lines where redaction would
// never finish. Rules run on .NET regular expressions.
// Written from public documentation and specifications, not from another project's source.

namespace DriftBusterOfflineRunner
{
    /// <summary>The runner log: each message stamped <c>[%Y-%m-%dT%H:%M:%SZ]</c> in UTC when it is written.</summary>
    public sealed class RunLog
    {
        private readonly List<string> _entries = new List<string>();

        public List<string> Entries
        {
            get { return _entries; }
        }

        public static string Stamp()
        {
            return DateTime.UtcNow.ToString("yyyy'-'MM'-'dd'T'HH':'mm':'ss'Z'", CultureInfo.InvariantCulture);
        }

        public void Write(string message)
        {
            _entries.Add("[" + Stamp() + "] " + message);
        }

        /// <summary><c>_write_log</c>: the entries joined by new lines, a final new line when there are entries, as text.</summary>
        public void Save(string path)
        {
            var text = string.Join("\n", _entries.ToArray()) + (_entries.Count > 0 ? "\n" : string.Empty);
            EngineFile.WriteText(path, text);
        }
    }

    public sealed class SecretRule
    {
        public SecretRule(string name, Regex pattern, string description)
        {
            Name = name;
            Pattern = pattern;
            Description = description;
        }

        public string Name { get; private set; }

        public Regex Pattern { get; private set; }

        public string Description { get; private set; }
    }

    public sealed class SecretFinding
    {
        public SecretFinding(string path, string rule, int line, string snippet)
        {
            Path = path;
            Rule = rule;
            Line = line;
            Snippet = snippet;
        }

        public string Path { get; private set; }

        public string Rule { get; private set; }

        public int Line { get; private set; }

        public string Snippet { get; private set; }
    }

    /// <summary>A rule stopped on one line because the line went past the guard budget.</summary>
    public sealed class SecretRedactionGuard
    {
        public SecretRedactionGuard(string path, string rule, int line)
        {
            Path = path;
            Rule = rule;
            Line = line;
        }

        public string Path { get; private set; }

        public string Rule { get; private set; }

        public int Line { get; private set; }
    }

    public sealed class CompiledRuleset
    {
        public CompiledRuleset(List<SecretRule> rules, string version)
        {
            Rules = rules;
            Version = version;
        }

        public List<SecretRule> Rules { get; private set; }

        public string Version { get; private set; }
    }

    public sealed class SecretContext
    {
        public SecretContext()
        {
            Rules = new List<SecretRule>();
            Version = string.Empty;
            IgnoreRules = new HashSet<string>(StringComparer.Ordinal);
            IgnorePatterns = new List<Regex>();
            IgnorePatternText = new List<string>();
            Findings = new List<SecretFinding>();
            RedactionGuards = new List<SecretRedactionGuard>();
        }

        public List<SecretRule> Rules { get; set; }

        public string Version { get; set; }

        public HashSet<string> IgnoreRules { get; set; }

        public List<Regex> IgnorePatterns { get; set; }

        public List<string> IgnorePatternText { get; set; }

        public List<SecretFinding> Findings { get; set; }

        public bool RulesLoaded { get; set; }

        public List<SecretRedactionGuard> RedactionGuards { get; set; }
    }

    public sealed class CopyResult
    {
        public CopyResult(long size, string sha256)
        {
            Size = size;
            Sha256 = sha256;
        }

        public long Size { get; private set; }

        public string Sha256 { get; private set; }
    }

    /// <summary>Text files as pathlib reads and writes them (UTF-8, universal new lines).</summary>
    public static class EngineFile
    {
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        /// <summary>Path.read_text(encoding="utf-8"): strict decoding, "\r\n" and "\r" read as "\n".</summary>
        public static string ReadText(string path)
        {
            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(EngineOs.Abs(path));
            }
            catch (FileNotFoundException)
            {
                throw new EngineException("FileNotFoundException", "Could not find file '" + path + "'.");
            }
            catch (DirectoryNotFoundException)
            {
                throw new EngineException("DirectoryNotFoundException", "Could not find a part of the path '" + path + "'.");
            }

            var text = EngineUtf8Decoder.DecodeStrict(bytes);
            return text.Replace("\r\n", "\n").Replace('\r', '\n');
        }

        /// <summary>Path.write_text(text, encoding="utf-8"): "\n" written as the platform's line break, strict encoding.</summary>
        public static void WriteText(string path, string text)
        {
            if (Environment.NewLine != "\n")
            {
                text = text.Replace("\n", Environment.NewLine);
            }

            WriteBytes(path, EncodeUtf8(text));
        }

        public static byte[] EncodeUtf8(string text)
        {
            try
            {
                return StrictUtf8.GetBytes(text);
            }
            catch (EncoderFallbackException)
            {
                throw new EngineException("InvalidDataException", "The text holds an unpaired surrogate and cannot be written as UTF-8.");
            }
        }

        public static void WriteBytes(string path, byte[] bytes)
        {
            File.WriteAllBytes(EngineOs.Abs(path), bytes);
        }

        public static string HashFile(string path)
        {
            using (var stream = new FileStream(EngineOs.Abs(path), FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var sha = SHA256.Create())
            {
                return EngineText.Hex(sha.ComputeHash(stream));
            }
        }

        private static readonly MethodInfo GetUnixFileMode = typeof(File).GetMethod("GetUnixFileMode", new[] { typeof(string) });
        private static readonly MethodInfo SetUnixFileMode = FindSetUnixFileMode();

        private static MethodInfo FindSetUnixFileMode()
        {
            foreach (var method in typeof(File).GetMethods())
            {
                if (method.Name == "SetUnixFileMode" && method.GetParameters().Length == 2 && method.GetParameters()[0].ParameterType == typeof(string))
                {
                    return method;
                }
            }

            return null;
        }

        /// <summary>shutil.copystat, best effort: permission bits (off Windows) and access and modification times.</summary>
        public static void CopyStat(string source, string destination)
        {
            try
            {
                var from = EngineOs.Abs(source);
                var to = EngineOs.Abs(destination);
                if (!EngineOs.Windows && GetUnixFileMode != null && SetUnixFileMode != null)
                {
                    SetUnixFileMode.Invoke(null, new[] { to, GetUnixFileMode.Invoke(null, new object[] { from }) });
                }

                File.SetLastAccessTimeUtc(to, File.GetLastAccessTimeUtc(from));
                File.SetLastWriteTimeUtc(to, File.GetLastWriteTimeUtc(from));
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
            catch (TargetInvocationException)
            {
            }
        }

        /// <summary>shutil.copy2(source, destination) followed by the destination's size and SHA-256.</summary>
        public static CopyResult CopyVerbatim(string source, string destination)
        {
            File.Copy(EngineOs.Abs(source), EngineOs.Abs(destination), true);
            CopyStat(source, destination);
            return new CopyResult(new FileInfo(EngineOs.Abs(destination)).Length, HashFile(destination));
        }
    }

    public static class SecretScanner
    {
        private const string Redaction = "[SECRET]";

        /// <summary>Replacements inside inserted text one line may take since its last replacement that consumed source text.</summary>
        public const int GuardBudget = 1024;

        /// <summary>The packaged ruleset load_secret_rules caches for the session (set by the runner; null before the first load).</summary>
        public static object PackagedRules { get; set; }

        /// <summary>_compile_ruleset_from_mapping(payload): null when nothing compiles.</summary>
        public static CompiledRuleset CompileRuleset(object payload)
        {
            payload = Engine.Unwrap(payload);
            if (!Engine.Truthy(payload) || !Engine.IsMapping(payload))
            {
                return null;
            }

            var rulesPayload = Engine.Get(payload, "rules", null);
            if (!Engine.IsSequence(rulesPayload))
            {
                return null;
            }

            var rules = new List<SecretRule>();
            foreach (var entry in Engine.Iterate(rulesPayload))
            {
                if (!Engine.IsMapping(entry))
                {
                    continue;
                }

                var name = EngineText.Strip(Engine.Str(Engine.Or(Engine.Get(entry, "name", null), string.Empty)));
                var patternText = Engine.Get(entry, "pattern", null);
                if (name.Length == 0 || !Engine.Truthy(patternText))
                {
                    continue;
                }

                var flags = EngineText.Lower(Engine.Str(Engine.Or(Engine.Get(entry, "flags", null), string.Empty)));
                var options = RegexOptions.CultureInvariant;
                if (flags.IndexOf('i') >= 0)
                {
                    options |= RegexOptions.IgnoreCase;
                }

                Regex pattern;
                try
                {
                    pattern = new Regex(Engine.Str(patternText), options);
                }
                catch (ArgumentException)
                {
                    continue;
                }

                var description = Engine.Get(entry, "description", null);
                rules.Add(new SecretRule(name, pattern, Engine.Truthy(description) ? Engine.Str(description) : null));
            }

            if (rules.Count == 0)
            {
                return null;
            }

            return new CompiledRuleset(rules, Engine.Str(Engine.Or(Engine.Get(payload, "version", null), string.Empty)));
        }

        /// <summary>secret_option_values(value).</summary>
        public static List<string> OptionValues(object value)
        {
            value = Engine.Unwrap(value);
            var result = new List<string>();
            if (!Engine.Truthy(value))
            {
                return result;
            }

            var text = value as string;
            if (text != null)
            {
                foreach (var part in EngineText.SplitSpaceCommaSemicolon(text))
                {
                    var stripped = EngineText.Strip(part);
                    if (stripped.Length > 0)
                    {
                        result.Add(stripped);
                    }
                }

                return result;
            }

            if (Engine.IsList(value))
            {
                foreach (var item in Engine.Iterate(value))
                {
                    if (item == null)
                    {
                        continue;
                    }

                    var stripped = EngineText.Strip(Engine.Str(item));
                    if (stripped.Length > 0)
                    {
                        result.Add(stripped);
                    }
                }
            }

            return result;
        }

        /// <summary>build_context's ignore values and patterns over a compiled ruleset.</summary>
        public static SecretContext BuildContext(object options, object secretScanner, CompiledRuleset rules, bool loaded)
        {
            var context = new SecretContext();
            context.Rules = rules == null ? new List<SecretRule>() : rules.Rules;
            context.Version = rules == null ? string.Empty : rules.Version;
            if (Engine.Truthy(options))
            {
                foreach (var value in OptionValues(Engine.Get(options, "secret_ignore_rules", null)))
                {
                    context.IgnoreRules.Add(value);
                }
            }

            if (Engine.Truthy(secretScanner))
            {
                foreach (var value in OptionValues(Engine.Get(secretScanner, "ignore_rules", null)))
                {
                    context.IgnoreRules.Add(value);
                }
            }

            var sources = new List<string>();
            if (Engine.Truthy(options))
            {
                sources.AddRange(OptionValues(Engine.Get(options, "secret_ignore_patterns", null)));
            }

            if (Engine.Truthy(secretScanner))
            {
                sources.AddRange(OptionValues(Engine.Get(secretScanner, "ignore_patterns", null)));
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var text in sources)
            {
                if (!seen.Add(text))
                {
                    continue;
                }

                context.IgnorePatternText.Add(text);
                try
                {
                    context.IgnorePatterns.Add(new Regex(text, RegexOptions.CultureInvariant));
                }
                catch (ArgumentException)
                {
                }
            }

            context.RulesLoaded = loaded && context.Rules.Count > 0;
            return context;
        }

        /// <summary>looks_binary(path): a NUL byte in the first 1024 bytes; false when the file cannot be read.</summary>
        public static bool LooksBinary(string path)
        {
            try
            {
                using (var stream = new FileStream(EngineOs.Abs(path), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    var buffer = new byte[1024];
                    var total = 0;
                    int read;
                    while (total < buffer.Length && (read = stream.Read(buffer, total, buffer.Length - total)) > 0)
                    {
                        total += read;
                    }

                    return Array.IndexOf(buffer, (byte)0, 0, total) >= 0;
                }
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        /// <summary>copy_with_secret_filter(source, destination, display_path=..., context=..., log=...).</summary>
        public static CopyResult CopyWithSecretFilter(string source, string destination, string displayPath, SecretContext context, RunLog log)
        {
            var parent = Path.GetDirectoryName(EngineOs.Abs(destination));
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            if (!context.RulesLoaded || context.Rules.Count == 0 || LooksBinary(source))
            {
                return EngineFile.CopyVerbatim(source, destination);
            }

            var buffered = new List<string>();
            var sanitising = false;
            var matches = 0;
            var lineNumber = 0;
            foreach (var line in ReadUniversalLines(source))
            {
                lineNumber++;
                var redaction = new LineRedaction(line, context.Findings.Count);
                HashSet<SecretRule> stopped = null;
                while (true)
                {
                    SecretRule rule;
                    Match match;
                    if (!FirstTriggeredRule(context, redaction.Working, line, stopped, out rule, out match))
                    {
                        break;
                    }

                    var start = match.Index;
                    var end = match.Index + match.Length;
                    if (redaction.ExceedsGuardBudget(rule, start, end))
                    {
                        if (stopped == null)
                        {
                            stopped = new HashSet<SecretRule>();
                        }

                        foreach (var looping in redaction.RollBack(context.Findings))
                        {
                            stopped.Add(looping);
                            context.RedactionGuards.Add(new SecretRedactionGuard(displayPath, looping.Name, lineNumber));
                        }

                        continue;
                    }

                    sanitising = true;
                    var redacted = redaction.Replace(start, end);
                    var preview = redacted.TrimEnd('\n', '\r');
                    var masked = EngineText.CodePointLength(preview) > 120 ? EngineText.CodePointPrefix(preview, 117) + "..." : preview;
                    context.Findings.Add(new SecretFinding(displayPath, rule.Name, lineNumber, EngineText.CodePointPrefix(preview, 200)));
                    redaction.Record(
                        "secret candidate redacted (" + rule.Name + ") from " + displayPath + ":"
                        + lineNumber.ToString(CultureInfo.InvariantCulture) + " -> " + masked);
                    if (start == end)
                    {
                        break;
                    }
                }

                foreach (var message in redaction.Logs)
                {
                    log.Write(message);
                }

                matches += redaction.Logs.Count;
                buffered.Add(redaction.Logs.Count > 0 ? redaction.Working : line);
            }

            if (!sanitising)
            {
                return EngineFile.CopyVerbatim(source, destination);
            }

            var builder = new StringBuilder();
            foreach (var line in buffered)
            {
                builder.Append(line);
            }

            EngineFile.WriteText(destination, builder.ToString());
            EngineFile.CopyStat(source, destination);
            log.Write("scrubbed " + matches.ToString(CultureInfo.InvariantCulture) + " potential secret line(s) from " + displayPath);
            return new CopyResult(new FileInfo(EngineOs.Abs(destination)).Length, EngineFile.HashFile(destination));
        }

        private static bool FirstTriggeredRule(
            SecretContext context,
            string working,
            string original,
            HashSet<SecretRule> stopped,
            out SecretRule rule,
            out Match match)
        {
            foreach (var candidate in context.Rules)
            {
                if (context.IgnoreRules.Contains(candidate.Name) || (stopped != null && stopped.Contains(candidate)))
                {
                    continue;
                }

                var found = candidate.Pattern.Match(working);
                if (!found.Success)
                {
                    continue;
                }

                var ignored = false;
                foreach (var pattern in context.IgnorePatterns)
                {
                    if (pattern.IsMatch(original))
                    {
                        ignored = true;
                        break;
                    }
                }

                if (ignored)
                {
                    continue;
                }

                rule = candidate;
                match = found;
                return true;
            }

            rule = null;
            match = null;
            return false;
        }

        // open(encoding="utf-8", errors="replace") iterated by line: "\r\n" and "\r" read as "\n", each line keeping its "\n".
        private static IEnumerable<string> ReadUniversalLines(string path)
        {
            using (var stream = new FileStream(EngineOs.Abs(path), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                var decoder = new EngineUtf8Decoder();
                var bytes = new byte[1 << 16];
                var buffer = new StringBuilder(1 << 16);
                var line = new StringBuilder();
                var afterCarriageReturn = false;
                var final = false;
                while (!final)
                {
                    var read = stream.Read(bytes, 0, bytes.Length);
                    final = read == 0;
                    buffer.Length = 0;
                    decoder.Decode(bytes, 0, read, final, buffer);
                    for (var index = 0; index < buffer.Length; index++)
                    {
                        var ch = buffer[index];
                        if (afterCarriageReturn)
                        {
                            afterCarriageReturn = false;
                            if (ch == '\n')
                            {
                                continue;
                            }
                        }

                        if (ch == '\r' || ch == '\n')
                        {
                            afterCarriageReturn = ch == '\r';
                            line.Append('\n');
                            yield return line.ToString();
                            line.Length = 0;
                            continue;
                        }

                        line.Append(ch);
                    }
                }

                if (line.Length > 0)
                {
                    yield return line.ToString();
                }
            }
        }

        private sealed class LineRedaction
        {
            private readonly int _findingsAtStart;
            private readonly List<SecretRule> _looping = new List<SecretRule>();
            private List<bool> _inserted;
            private int _budgetUsed;
            private bool _consumedSource;
            private string _checkpointWorking;
            private List<bool> _checkpointInserted;
            private int _checkpointLogs;

            public LineRedaction(string line, int findingsAtStart)
            {
                _findingsAtStart = findingsAtStart;
                _inserted = new List<bool>(new bool[line.Length]);
                _checkpointWorking = line;
                _checkpointInserted = new List<bool>(new bool[line.Length]);
                Working = line;
                Logs = new List<string>();
            }

            public string Working { get; private set; }

            public List<string> Logs { get; private set; }

            public bool ExceedsGuardBudget(SecretRule rule, int start, int end)
            {
                if (start == end || _inserted.GetRange(start, end - start).Contains(false))
                {
                    return false;
                }

                if (end - start <= Redaction.Length && ++_budgetUsed > GuardBudget)
                {
                    return true;
                }

                if (!_looping.Contains(rule))
                {
                    _looping.Add(rule);
                }

                return false;
            }

            public string Replace(int start, int end)
            {
                _consumedSource = _inserted.GetRange(start, end - start).Contains(false);
                Working = Working.Substring(0, start) + Redaction + Working.Substring(end);
                _inserted.RemoveRange(start, end - start);
                var added = new bool[Redaction.Length];
                for (var index = 0; index < added.Length; index++)
                {
                    added[index] = true;
                }

                _inserted.InsertRange(start, added);
                return Working;
            }

            public void Record(string message)
            {
                Logs.Add(message);
                if (_consumedSource)
                {
                    _checkpointWorking = Working;
                    _checkpointInserted = new List<bool>(_inserted);
                    _checkpointLogs = Logs.Count;
                    _budgetUsed = 0;
                    _looping.Clear();
                }
            }

            public List<SecretRule> RollBack(List<SecretFinding> findings)
            {
                var keep = _findingsAtStart + _checkpointLogs;
                while (findings.Count > keep)
                {
                    findings.RemoveAt(findings.Count - 1);
                }

                Logs.RemoveRange(_checkpointLogs, Logs.Count - _checkpointLogs);
                Working = _checkpointWorking;
                _inserted = new List<bool>(_checkpointInserted);
                var looping = new List<SecretRule>(_looping);
                _looping.Clear();
                _budgetUsed = 0;
                return looping;
            }
        }
    }
}

// The SQLite snapshot export over the SQLite C API: Windows' built-in winsqlite3.dll, or libsqlite3.so.0 on Linux (tests).
// Each value is read by its storage class.
// Written from the publicly documented SQLite C interface, not from another project's source.

namespace DriftBusterOfflineRunner
{
    /// <summary>
    /// winreg.EnumValue's data for a value RegistryKey.GetValue does not read: the value's type and raw bytes through RegQueryValueExW
    /// (RegistryKey returns null for REG_LINK, REG_RESOURCE_LIST, REG_FULL_RESOURCE_DESCRIPTOR, REG_RESOURCE_REQUIREMENTS_LIST and
    /// non-standard type numbers, all of which winreg returns as bytes).
    /// </summary>
    public static class EngineWinreg
    {
        private const int MoreData = 234;

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegQueryValueExW")]
        private static extern int RegQueryValueEx(SafeHandle key, string name, IntPtr reserved, out int type, byte[] data, ref int size);

        public static byte[] QueryRaw(SafeHandle key, string name, out int type)
        {
            var size = 0;
            var rc = RegQueryValueEx(key, name, IntPtr.Zero, out type, null, ref size);
            while (rc == 0 || rc == MoreData)
            {
                var data = new byte[size];
                var capacity = size;
                rc = RegQueryValueEx(key, name, IntPtr.Zero, out type, data, ref size);
                if (rc == 0 && size <= capacity)
                {
                    if (size < capacity)
                    {
                        Array.Resize(ref data, size);
                    }

                    return data;
                }

                if (rc == 0)
                {
                    rc = MoreData;
                }
            }

            throw new IOException("RegQueryValueEx failed", new Win32Exception(rc));
        }
    }

    internal static class WinSqlite3
    {
        private const string Library = "winsqlite3";

        [DllImport(Library, CallingConvention = CallingConvention.Winapi)]
        internal static extern int sqlite3_open_v2(byte[] filename, out IntPtr db, int flags, IntPtr vfs);

        [DllImport(Library, CallingConvention = CallingConvention.Winapi)]
        internal static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int bytes, out IntPtr statement, IntPtr tail);

        [DllImport(Library, CallingConvention = CallingConvention.Winapi)]
        internal static extern int sqlite3_step(IntPtr statement);

        [DllImport(Library, CallingConvention = CallingConvention.Winapi)]
        internal static extern int sqlite3_column_count(IntPtr statement);

        [DllImport(Library, CallingConvention = CallingConvention.Winapi)]
        internal static extern IntPtr sqlite3_column_name(IntPtr statement, int column);

        [DllImport(Library, CallingConvention = CallingConvention.Winapi)]
        internal static extern int sqlite3_column_type(IntPtr statement, int column);

        [DllImport(Library, CallingConvention = CallingConvention.Winapi)]
        internal static extern long sqlite3_column_int64(IntPtr statement, int column);

        [DllImport(Library, CallingConvention = CallingConvention.Winapi)]
        internal static extern double sqlite3_column_double(IntPtr statement, int column);

        [DllImport(Library, CallingConvention = CallingConvention.Winapi)]
        internal static extern IntPtr sqlite3_column_text(IntPtr statement, int column);

        [DllImport(Library, CallingConvention = CallingConvention.Winapi)]
        internal static extern IntPtr sqlite3_column_blob(IntPtr statement, int column);

        [DllImport(Library, CallingConvention = CallingConvention.Winapi)]
        internal static extern int sqlite3_column_bytes(IntPtr statement, int column);

        [DllImport(Library, CallingConvention = CallingConvention.Winapi)]
        internal static extern int sqlite3_finalize(IntPtr statement);

        [DllImport(Library, CallingConvention = CallingConvention.Winapi)]
        internal static extern int sqlite3_close(IntPtr db);

        [DllImport(Library, CallingConvention = CallingConvention.Winapi)]
        internal static extern IntPtr sqlite3_errmsg(IntPtr db);
    }

    internal static class LibSqlite3
    {
        private const string Library = "libsqlite3.so.0";

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_open_v2(byte[] filename, out IntPtr db, int flags, IntPtr vfs);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int bytes, out IntPtr statement, IntPtr tail);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_step(IntPtr statement);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_column_count(IntPtr statement);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr sqlite3_column_name(IntPtr statement, int column);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_column_type(IntPtr statement, int column);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern long sqlite3_column_int64(IntPtr statement, int column);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern double sqlite3_column_double(IntPtr statement, int column);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr sqlite3_column_text(IntPtr statement, int column);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr sqlite3_column_blob(IntPtr statement, int column);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_column_bytes(IntPtr statement, int column);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_finalize(IntPtr statement);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_close(IntPtr db);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr sqlite3_errmsg(IntPtr db);
    }

    /// <summary>A result set: column names and rows of Python values (null, BigInteger, double, string, byte[]).</summary>
    public sealed class SqliteRows
    {
        public SqliteRows()
        {
            Columns = new List<string>();
            Rows = new List<object[]>();
        }

        public List<string> Columns { get; private set; }

        public List<object[]> Rows { get; private set; }

        /// <summary>sqlite3.Row[name]: the first column whose name equals <paramref name="name"/> ignoring ASCII case.</summary>
        public object Lookup(object[] row, string name)
        {
            for (var index = 0; index < Columns.Count; index++)
            {
                if (EqualIgnoreAsciiCase(Columns[index], name))
                {
                    return row[index];
                }
            }

            throw new EngineException("KeyNotFoundException", "The row has no column named '" + name + "'.");
        }

        private static bool EqualIgnoreAsciiCase(string left, string right)
        {
            if (left.Length != right.Length)
            {
                return false;
            }

            for (var index = 0; index < left.Length; index++)
            {
                var a = left[index];
                var b = right[index];
                if (a >= 'A' && a <= 'Z')
                {
                    a = (char)(a + 32);
                }

                if (b >= 'A' && b <= 'Z')
                {
                    b = (char)(b + 32);
                }

                if (a != b)
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>A SQLite connection through the platform library.</summary>
    public sealed class SqliteDatabase : IDisposable
    {
        public const int OpenReadOnly = 0x00000001;
        public const int OpenReadWrite = 0x00000002;
        public const int OpenCreate = 0x00000004;

        private const int Row = 100;
        private const int Done = 101;

        private IntPtr _db;

        private SqliteDatabase(IntPtr db)
        {
            _db = db;
        }

        private static bool Linux
        {
            get { return !EngineOs.Windows; }
        }

        /// <summary>The library name used on this platform.</summary>
        public static string LibraryName
        {
            get { return Linux ? "libsqlite3.so.0" : "winsqlite3"; }
        }

        public static SqliteDatabase Open(string path, int flags)
        {
            IntPtr db;
            var name = Nul(Encoding.UTF8.GetBytes(EngineOs.Abs(path)));
            var rc = Linux ? LibSqlite3.sqlite3_open_v2(name, out db, flags, IntPtr.Zero) : WinSqlite3.sqlite3_open_v2(name, out db, flags, IntPtr.Zero);
            if (rc != 0)
            {
                var message = db == IntPtr.Zero ? "unable to open database file" : ErrorMessage(db);
                if (db != IntPtr.Zero)
                {
                    Close(db);
                }

                throw new EngineException(ErrorClass(rc & 0xFF), message);
            }

            return new SqliteDatabase(db);
        }

        private static byte[] Nul(byte[] bytes)
        {
            var result = new byte[bytes.Length + 1];
            Buffer.BlockCopy(bytes, 0, result, 0, bytes.Length);
            return result;
        }

        private static void Close(IntPtr db)
        {
            if (Linux)
            {
                LibSqlite3.sqlite3_close(db);
            }
            else
            {
                WinSqlite3.sqlite3_close(db);
            }
        }

        private static string ErrorMessage(IntPtr db)
        {
            var pointer = Linux ? LibSqlite3.sqlite3_errmsg(db) : WinSqlite3.sqlite3_errmsg(db);
            return Utf8At(pointer, -1);
        }

        private static string Utf8At(IntPtr pointer, int length)
        {
            bool invalid;
            return Utf8At(pointer, length, out invalid);
        }

        private static string Utf8At(IntPtr pointer, int length, out bool invalid)
        {
            invalid = false;
            if (pointer == IntPtr.Zero)
            {
                return null;
            }

            if (length < 0)
            {
                length = 0;
                while (Marshal.ReadByte(pointer, length) != 0)
                {
                    length++;
                }
            }

            var bytes = new byte[length];
            Marshal.Copy(pointer, bytes, 0, length);
            return EngineUtf8Decoder.Decode(bytes, out invalid);
        }

        /// <summary>The .NET exception type the backend raises for a primary result code.</summary>
        public static string ErrorClass(int code)
        {
            return code == 7 ? "InsufficientMemoryException" : "SqliteException";
        }

        /// <summary>cursor.execute(sql).fetchall(): every row, each value by its storage class.</summary>
        public SqliteRows FetchAll(string sql)
        {
            var bytes = Encoding.UTF8.GetBytes(sql);
            IntPtr statement;
            var rc = Linux
                ? LibSqlite3.sqlite3_prepare_v2(_db, bytes, bytes.Length, out statement, IntPtr.Zero)
                : WinSqlite3.sqlite3_prepare_v2(_db, bytes, bytes.Length, out statement, IntPtr.Zero);
            if (rc != 0)
            {
                throw new EngineException(ErrorClass(rc & 0xFF), ErrorMessage(_db));
            }

            var rows = new SqliteRows();
            if (statement == IntPtr.Zero)
            {
                return rows;
            }

            try
            {
                var count = Linux ? LibSqlite3.sqlite3_column_count(statement) : WinSqlite3.sqlite3_column_count(statement);
                for (var column = 0; column < count; column++)
                {
                    var name = Linux ? LibSqlite3.sqlite3_column_name(statement, column) : WinSqlite3.sqlite3_column_name(statement, column);
                    rows.Columns.Add(Utf8At(name, -1) ?? string.Empty);
                }

                while (true)
                {
                    rc = Linux ? LibSqlite3.sqlite3_step(statement) : WinSqlite3.sqlite3_step(statement);
                    if (rc == Done)
                    {
                        break;
                    }

                    if (rc != Row)
                    {
                        throw new EngineException(ErrorClass(rc & 0xFF), ErrorMessage(_db));
                    }

                    var values = new object[count];
                    for (var column = 0; column < count; column++)
                    {
                        values[column] = ReadValue(statement, column, rows.Columns[column]);
                    }

                    rows.Rows.Add(values);
                }
            }
            finally
            {
                if (Linux)
                {
                    LibSqlite3.sqlite3_finalize(statement);
                }
                else
                {
                    WinSqlite3.sqlite3_finalize(statement);
                }
            }

            return rows;
        }

        private static object ReadValue(IntPtr statement, int column, string columnName)
        {
            var type = Linux ? LibSqlite3.sqlite3_column_type(statement, column) : WinSqlite3.sqlite3_column_type(statement, column);
            switch (type)
            {
                case 1:
                    return new BigInteger(Linux ? LibSqlite3.sqlite3_column_int64(statement, column) : WinSqlite3.sqlite3_column_int64(statement, column));
                case 2:
                    return Linux ? LibSqlite3.sqlite3_column_double(statement, column) : WinSqlite3.sqlite3_column_double(statement, column);
                case 3:
                {
                    var text = Linux ? LibSqlite3.sqlite3_column_text(statement, column) : WinSqlite3.sqlite3_column_text(statement, column);
                    var length = Linux ? LibSqlite3.sqlite3_column_bytes(statement, column) : WinSqlite3.sqlite3_column_bytes(statement, column);
                    bool invalid;
                    var decoded = Utf8At(text, length, out invalid) ?? string.Empty;
                    if (invalid)
                    {
                        throw new EngineException("SqliteException", "Could not decode to UTF-8 column '" + columnName + "' with text '" + decoded + "'");
                    }

                    return decoded;
                }

                case 4:
                {
                    var blob = Linux ? LibSqlite3.sqlite3_column_blob(statement, column) : WinSqlite3.sqlite3_column_blob(statement, column);
                    var length = Linux ? LibSqlite3.sqlite3_column_bytes(statement, column) : WinSqlite3.sqlite3_column_bytes(statement, column);
                    var bytes = new byte[length];
                    if (length > 0)
                    {
                        Marshal.Copy(blob, bytes, 0, length);
                    }

                    return bytes;
                }

                default:
                    return null;
            }
        }

        public void Dispose()
        {
            if (_db != IntPtr.Zero)
            {
                Close(_db);
                _db = IntPtr.Zero;
            }
        }
    }

    public static class SqlSnapshots
    {
        /// <summary>_hash_text(value, salt=salt).</summary>
        public static string HashText(object value, string salt)
        {
            var text = EngineJson.Dumps(value, -1, true, true);
            var saltBytes = EngineFile.EncodeUtf8(salt);
            var textBytes = EngineFile.EncodeUtf8(text);
            var combined = new byte[saltBytes.Length + textBytes.Length];
            Buffer.BlockCopy(saltBytes, 0, combined, 0, saltBytes.Length);
            Buffer.BlockCopy(textBytes, 0, combined, saltBytes.Length, textBytes.Length);
            return "sha256:" + EngineText.Sha256Hex(combined);
        }

        /// <summary>_normalise_value(value): bytes as {"type": "base64", "value": ...}; everything else as read.</summary>
        public static object NormaliseValue(object value)
        {
            var bytes = value as byte[];
            if (bytes == null)
            {
                return value;
            }

            var payload = new OrderedDictionary(StringComparer.Ordinal);
            payload["type"] = "base64";
            payload["value"] = Convert.ToBase64String(bytes);
            return payload;
        }

        /// <summary>datetime.now(UTC).isoformat().</summary>
        public static string CapturedAt()
        {
            var now = DateTime.UtcNow;
            var micro = (int)((now.Ticks % TimeSpan.TicksPerSecond) / 10);
            var text = now.ToString("yyyy'-'MM'-'dd'T'HH':'mm':'ss", CultureInfo.InvariantCulture);
            if (micro != 0)
            {
                text += "." + micro.ToString("000000", CultureInfo.InvariantCulture);
            }

            return text + "+00:00";
        }

        private static List<string> Columns(IDictionary map, string table)
        {
            var result = new List<string>();
            if (map == null || !map.Contains(table))
            {
                return result;
            }

            foreach (var column in Engine.Iterate(map[table]))
            {
                result.Add(Engine.Str(column));
            }

            return result;
        }

        /// <summary>
        /// build_sqlite_snapshot(path, tables=..., exclude_tables=..., mask_columns=..., hash_columns=..., limit=..., placeholder=...,
        /// hash_salt=...).to_dict(), the database opened read-only. <paramref name="path"/> is str(Path(path)); the column maps hold, per
        /// table, the column names.
        /// </summary>
        public static OrderedDictionary Build(
            string path,
            IList tables,
            IList excludeTables,
            IDictionary maskColumns,
            IDictionary hashColumns,
            object limit,
            string placeholder,
            string hashSalt)
        {
            limit = Engine.Unwrap(limit);
            if (limit != null && Engine.LessOrEqualZero(limit))
            {
                throw new EngineException("ArgumentOutOfRangeException", "limit must be positive when provided. (Parameter 'limit')");
            }

            if (!EngineFs.Exists(path))
            {
                throw new EngineException("FileNotFoundException", "Database not found: " + path);
            }

            var include = new HashSet<string>(StringComparer.Ordinal);
            if (tables != null)
            {
                foreach (var name in tables)
                {
                    if (Engine.Truthy(name))
                    {
                        include.Add(Engine.Str(name));
                    }
                }
            }

            var excluded = new HashSet<string>(StringComparer.Ordinal);
            if (excludeTables != null)
            {
                foreach (var name in excludeTables)
                {
                    if (Engine.Truthy(name))
                    {
                        excluded.Add(Engine.Str(name));
                    }
                }
            }

            if (EngineFs.IsDir(path))
            {
                throw new EngineException("SqliteException", "unable to open database file");
            }

            var exported = new List<object>();
            using (var database = SqliteDatabase.Open(path, SqliteDatabase.OpenReadOnly))
            {
                var master = database.FetchAll("SELECT name, sql FROM sqlite_master WHERE type = 'table' ORDER BY name");
                foreach (var row in master.Rows)
                {
                    if (row[0] is byte[])
                    {
                        throw new EngineException("InvalidDataException", "A table name stored as a BLOB cannot be exported.");
                    }

                    var name = row[0] as string;
                    if (name == null)
                    {
                        throw new EngineException("InvalidDataException", "expected a table name string, not '" + Engine.TypeName(row[0]) + "'");
                    }

                    if (name.StartsWith("sqlite_", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if ((include.Count > 0 && !include.Contains(name)) || excluded.Contains(name))
                    {
                        continue;
                    }

                    exported.Add(ExportTable(database, name, row[1], maskColumns, hashColumns, limit, placeholder, hashSalt));
                }
            }

            var snapshot = new OrderedDictionary(StringComparer.Ordinal);
            snapshot["database"] = EnginePath.Name(path);
            snapshot["dialect"] = "sqlite";
            snapshot["captured_at"] = CapturedAt();
            snapshot["path"] = path;
            snapshot["tables"] = exported;
            return snapshot;
        }

        private static OrderedDictionary ExportTable(
            SqliteDatabase database,
            string table,
            object schema,
            IDictionary maskColumns,
            IDictionary hashColumns,
            object limit,
            string placeholder,
            string hashSalt)
        {
            var info = database.FetchAll("PRAGMA table_info(" + table + ")");
            var columns = new List<object>();
            foreach (var row in info.Rows)
            {
                columns.Add(row[1]);
            }

            var limitClause = limit != null ? " LIMIT " + Engine.Int(limit).ToString(CultureInfo.InvariantCulture) : string.Empty;
            var fetched = database.FetchAll("SELECT * FROM " + table + limitClause);
            var masked = Columns(maskColumns, table);
            var hashed = Columns(hashColumns, table);
            var rows = new List<object>();
            foreach (var row in fetched.Rows)
            {
                var payload = new OrderedDictionary(StringComparer.Ordinal);
                foreach (var columnValue in columns)
                {
                    var column = Engine.Str(columnValue);
                    var value = fetched.Lookup(row, column);
                    if (masked.Contains(column))
                    {
                        payload[column] = placeholder;
                    }
                    else if (hashed.Contains(column))
                    {
                        payload[column] = HashText(value, table + "." + column + ":" + hashSalt);
                    }
                    else
                    {
                        payload[column] = NormaliseValue(value);
                    }
                }

                rows.Add(payload);
            }

            var count = database.FetchAll("SELECT COUNT(*) FROM " + table);
            var result = new OrderedDictionary(StringComparer.Ordinal);
            result["name"] = table;
            result["schema"] = schema;
            result["columns"] = columns;
            result["row_count"] = count.Rows[0][0];
            result["rows"] = rows;
            result["masked_columns"] = new List<object>(masked.ConvertAll(item => (object)item));
            result["hashed_columns"] = new List<object>(hashed.ConvertAll(item => (object)item));
            return result;
        }

        /// <summary>Runs statements on a read-write connection that creates the database (tests build fixtures with it).</summary>
        public static void Execute(string path, IList statements)
        {
            using (var database = SqliteDatabase.Open(path, SqliteDatabase.OpenReadWrite | SqliteDatabase.OpenCreate))
            {
                foreach (var statement in statements)
                {
                    database.FetchAll(Engine.Str(statement));
                }
            }
        }
    }
}
'@

    $arguments = @{ TypeDefinition = $source }
    if ($PSVersionTable.PSEdition -ne 'Core') {
        $arguments['ReferencedAssemblies'] = @('System.Core', 'System.Numerics')
    }

    Add-Type @arguments
}

# The engine exception a failed call raised: the EngineException itself or the first one inside the wrapping exceptions.
function Get-DbEngineException {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)] $ErrorRecord)

    $exception = $ErrorRecord
    if ($ErrorRecord -is [System.Management.Automation.ErrorRecord]) {
        $exception = $ErrorRecord.Exception
    }

    while ($null -ne $exception) {
        if ($exception -is [DriftBusterOfflineRunner.EngineException]) {
            return $exception
        }

        $exception = $exception.InnerException
    }

    return $null
}

function Get-DbEngineError {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string] $Type,
        [Parameter(Mandatory = $true)][AllowEmptyString()][string] $Message
    )

    return [EngineException]::new($Type, $Message)
}

# A PowerShell-side list that is never unrolled by the pipeline when returned with the unary comma.
function Get-DbList {
    [CmdletBinding()]
    param()

    return , ([System.Collections.Generic.List[object]]::new())
}

function Get-DbOrderedMap {
    [CmdletBinding()]
    param()

    return , ([System.Collections.Specialized.OrderedDictionary]::new([System.StringComparer]::Ordinal))
}

# offline_runner's config readers: OfflineRunnerConfig, OfflineRunnerProfile, the three source kinds, RemoteRegistryTarget,
# OfflineRunnerSettings and OfflineEncryptionSettings. Values stay in the engine's JSON domain (EngineJson.Loads) so truthiness, str() and
# int() behave consistently.

function Get-DbTimestamp {
    [CmdletBinding()]
    param()

    return [datetime]::UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", [System.Globalization.CultureInfo]::InvariantCulture)
}

function Get-DbStringTuple {
    # tuple(str(item) for item in value): the str() of each item.
    [CmdletBinding()]
    param($Value)

    $items = [System.Collections.Generic.List[string]]::new()
    foreach ($item in [Engine]::Iterate($Value)) {
        $items.Add([Engine]::Str($item))
    }

    return , $items.ToArray()
}

# _expand_path(text): os.path.expanduser(os.path.expandvars(text)) as str(Path(...)).
function Get-DbExpandedPath {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][AllowEmptyString()][string] $Text)

    return [EnginePath]::Normalise([EnginePath]::ExpandUser([EnginePath]::ExpandVars($Text)))
}

function ConvertFrom-DbOfflineCollectionSource {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)] $Payload)

    $path = [Engine]::Get($Payload, 'path', $null)
    if (-not [Engine]::Truthy($path) -or [EngineText]::Strip([Engine]::Str($path)).Length -eq 0) {
        throw (Get-DbEngineError 'InvalidDataException' "Source entry requires a non-empty 'path'.")
    }

    $alias = [Engine]::Get($Payload, 'alias', $null)
    if ($null -ne $alias -and [EngineText]::Strip([Engine]::Str($alias)).Length -eq 0) {
        $alias = $null
    }

    $optional = [Engine]::Truthy([Engine]::Get($Payload, 'optional', $false))
    $excludePayload = [Engine]::Get($Payload, 'exclude', $null)
    if ($excludePayload -is [string]) {
        $exclude = @([string]$excludePayload)
    }
    elseif ([Engine]::Truthy($excludePayload)) {
        $exclude = Get-DbStringTuple $excludePayload
    }
    else {
        $exclude = @()
    }

    return [pscustomobject]@{
        kind     = 'file'
        path     = [Engine]::Str($path)
        alias    = $(if ([Engine]::Truthy($alias)) { [Engine]::Str($alias) } else { $null })
        optional = $optional
        exclude  = $exclude
    }
}

function ConvertFrom-DbRegistryRootDescriptor {
    # registry.parse_registry_root_descriptor(text)
    [CmdletBinding()]
    param([AllowNull()][AllowEmptyString()][string] $Text)

    $value = [EngineText]::Strip([string]$Text)
    if ($value.Length -eq 0) {
        throw (Get-DbEngineError 'FormatException' 'Registry root descriptor must be non-empty')
    }

    $segments = [System.Collections.Generic.List[string]]::new()
    foreach ($segment in $value.Split(',')) {
        $stripped = [EngineText]::Strip($segment)
        if ($stripped.Length -gt 0) {
            $segments.Add($stripped)
        }
    }

    if ($segments.Count -eq 0) {
        throw (Get-DbEngineError 'FormatException' 'Registry root descriptor must be non-empty')
    }

    $base = $segments[0].Replace('/', '\')
    $match = [regex]::Match($base, '^(HKLM|HKCU)\\(.+)$', 'IgnoreCase, CultureInvariant')
    if (-not $match.Success) {
        throw (Get-DbEngineError 'FormatException' 'Registry root descriptor must start with HKLM\ or HKCU\')
    }

    $hive = [EngineText]::Upper($match.Groups[1].Value)
    $path = [EngineText]::Strip($match.Groups[2].Value)
    if ($path.Length -eq 0) {
        throw (Get-DbEngineError 'FormatException' 'Registry root path segment must be non-empty')
    }

    $view = $null
    for ($index = 1; $index -lt $segments.Count; $index++) {
        $option = $segments[$index]
        if ($option.IndexOf('=') -lt 0) {
            throw (Get-DbEngineError 'FormatException' "Registry root option '$option' must be formatted as key=value")
        }

        $cut = $option.IndexOf('=')
        $key = [EngineText]::Lower([EngineText]::Strip($option.Substring(0, $cut)))
        $rawValue = [EngineText]::Strip($option.Substring($cut + 1))
        if ($key -cne 'view') {
            throw (Get-DbEngineError 'FormatException' "Unsupported registry root option '$key'")
        }

        if ($rawValue.Length -eq 0) {
            throw (Get-DbEngineError 'FormatException' 'Registry root view must be non-empty when provided')
        }

        $normalised = [EngineText]::Upper($rawValue)
        if ($normalised -ceq 'AUTO') {
            $view = $null
        }
        elseif ($normalised -ceq '32' -or $normalised -ceq '64') {
            $view = $normalised
        }
        else {
            throw (Get-DbEngineError 'FormatException' 'Registry root view must be 32, 64, or auto')
        }
    }

    return [pscustomobject]@{ hive = $hive; path = $path; view = $view }
}

function ConvertTo-DbRegistryRootList {
    # offline_runner._normalise_registry_roots(value)
    [CmdletBinding()]
    param($Value)

    $roots = [System.Collections.Generic.List[object]]::new()
    if (-not [Engine]::Truthy($Value)) {
        return , $roots.ToArray()
    }

    if ($Value -is [string] -or [Engine]::IsMapping($Value) -or -not [Engine]::IsList($Value)) {
        $entries = @(, $Value)
    }
    else {
        $entries = [Engine]::Iterate($Value)
    }

    foreach ($entry in $entries) {
        if ($entry -is [string]) {
            $roots.Add((ConvertFrom-DbRegistryRootDescriptor $entry))
            continue
        }

        if ([Engine]::IsMapping($entry)) {
            $hive = [EngineText]::Strip([Engine]::Str([Engine]::Get($entry, 'hive', '')))
            $path = [EngineText]::Strip([Engine]::Str([Engine]::Get($entry, 'path', '')))
            if ($hive.Length -eq 0 -or $path.Length -eq 0) {
                throw (Get-DbEngineError 'InvalidDataException' "registry_scan roots entries require 'hive' and 'path'")
            }

            $viewRaw = [Engine]::Get($entry, 'view', $null)
            $view = $null
            if ($null -ne $viewRaw -and [EngineText]::Strip([Engine]::Str($viewRaw)).Length -gt 0) {
                $candidate = [EngineText]::Upper([EngineText]::Strip([Engine]::Str($viewRaw)))
                if ($candidate -ceq 'AUTO') {
                    $view = $null
                }
                elseif ($candidate -ceq '32' -or $candidate -ceq '64') {
                    $view = $candidate
                }
                else {
                    throw (Get-DbEngineError 'InvalidDataException' 'registry_scan root view must be 32, 64, or auto')
                }
            }

            $roots.Add([pscustomobject]@{ hive = [EngineText]::Upper($hive); path = $path; view = $view })
            continue
        }

        throw (Get-DbEngineError 'InvalidDataException' 'registry_scan roots entries must be strings or mappings')
    }

    return , $roots.ToArray()
}

function ConvertTo-DbRemoteBool {
    [CmdletBinding()]
    param($Value)

    if ($Value -is [bool]) {
        return $Value
    }

    $text = [EngineText]::Lower([EngineText]::Strip([Engine]::Str($Value)))
    if (@('1', 'true', 'yes', 'on') -ccontains $text) {
        return $true
    }

    if (@('0', 'false', 'no', 'off') -ccontains $text) {
        return $false
    }

    throw (Get-DbEngineError 'InvalidDataException' "Unsupported boolean value '$([Engine]::Str($Value))' for remote target")
}

function ConvertFrom-DbRemoteRegistryTarget {
    # RemoteRegistryTarget.from_payload(payload)
    [CmdletBinding()]
    param($Payload)

    if ($Payload -is [string]) {
        $host_ = [EngineText]::Strip($Payload)
        if ($host_.Length -eq 0) {
            throw (Get-DbEngineError 'InvalidDataException' 'remote target host must be non-empty')
        }

        return [pscustomobject]@{
            host = $host_; transport = 'winrm'; port = $null; use_ssl = $null; username = $null; password_env = $null
            credential_profile = $null; alias = $null
        }
    }

    if (-not [Engine]::IsMapping($Payload)) {
        throw (Get-DbEngineError 'InvalidDataException' 'remote target must be a string host or mapping')
    }

    $hostValue = [Engine]::Or([Engine]::Get($Payload, 'host', $null), [Engine]::Get($Payload, 'hostname', $null))
    if (-not [Engine]::Truthy($hostValue) -or [EngineText]::Strip([Engine]::Str($hostValue)).Length -eq 0) {
        throw (Get-DbEngineError 'InvalidDataException' "remote target requires 'host'")
    }

    if ([Engine]::Has($Payload, 'password')) {
        throw (Get-DbEngineError 'InvalidDataException' 'remote target must not embed raw passwords; use password_env')
    }

    $passwordEnvValue = $null
    if ([Engine]::Has($Payload, 'password_env')) {
        $passwordEnvValue = [Engine]::Get($Payload, 'password_env', $null)
    }
    elseif ([Engine]::Has($Payload, 'password-env')) {
        $passwordEnvValue = [Engine]::Get($Payload, 'password-env', $null)
    }

    if ($null -ne $passwordEnvValue -and [EngineText]::Strip([Engine]::Str($passwordEnvValue)).Length -eq 0) {
        throw (Get-DbEngineError 'InvalidDataException' 'remote target password_env must be non-empty when provided')
    }

    $usernameValue = [Engine]::Or([Engine]::Get($Payload, 'username', $null), [Engine]::Get($Payload, 'user', $null))
    $credentialProfile = $null
    if ([Engine]::Has($Payload, 'credential_profile')) {
        $credentialProfile = [Engine]::Get($Payload, 'credential_profile', $null)
    }
    elseif ([Engine]::Has($Payload, 'credential-profile')) {
        $credentialProfile = [Engine]::Get($Payload, 'credential-profile', $null)
    }

    $transportValue = [Engine]::Get($Payload, 'transport', 'winrm')
    $transport = $(if ([Engine]::Truthy($transportValue)) { [EngineText]::Lower([EngineText]::Strip([Engine]::Str($transportValue))) } else { 'winrm' })
    $aliasValue = [Engine]::Get($Payload, 'alias', $null)

    $portValue = [Engine]::Get($Payload, 'port', $null)
    $port = $null
    if ($null -ne $portValue) {
        $port = [Engine]::Int($portValue)
        if ($port.Sign -le 0) {
            throw (Get-DbEngineError 'InvalidDataException' 'remote target port must be positive')
        }
    }

    $useSslValue = $null
    if ([Engine]::Has($Payload, 'use_ssl')) {
        $useSslValue = [Engine]::Get($Payload, 'use_ssl', $null)
    }
    elseif ([Engine]::Has($Payload, 'use-ssl')) {
        $useSslValue = [Engine]::Get($Payload, 'use-ssl', $null)
    }

    $useSsl = $(if ($null -ne $useSslValue) { ConvertTo-DbRemoteBool $useSslValue } else { $null })

    return [pscustomobject]@{
        host               = [EngineText]::Strip([Engine]::Str($hostValue))
        transport          = $transport
        port               = $port
        use_ssl            = $useSsl
        username           = (Get-DbStrippedTruthy $usernameValue)
        password_env       = (Get-DbStrippedTruthy $passwordEnvValue)
        credential_profile = (Get-DbStrippedTruthy $credentialProfile)
        alias              = (Get-DbStrippedTruthy $aliasValue)
    }
}

# str(value).strip() if value and str(value).strip() else None
function Get-DbStrippedTruthy {
    [CmdletBinding()]
    param($Value)

    if ([Engine]::Truthy($Value)) {
        $text = [EngineText]::Strip([Engine]::Str($Value))
        if ($text.Length -gt 0) {
            return $text
        }
    }

    return $null
}

# re.split(r"[\s,;]+") parts for a str, stripped non-blank str() items for a list, nothing otherwise.
function Get-DbNormalisedSequence {
    [CmdletBinding()]
    param($Value)

    $items = [System.Collections.Generic.List[string]]::new()
    if (-not [Engine]::Truthy($Value)) {
        return , $items.ToArray()
    }

    if ($Value -is [string]) {
        foreach ($part in [EngineText]::SplitSpaceCommaSemicolon($Value)) {
            $items.Add($part)
        }

        return , $items.ToArray()
    }

    if ([Engine]::IsList($Value)) {
        foreach ($item in [Engine]::Iterate($Value)) {
            $text = [EngineText]::Strip([Engine]::Str($item))
            if ($text.Length -gt 0) {
                $items.Add($text)
            }
        }
    }

    return , $items.ToArray()
}

function ConvertFrom-DbOfflineRegistryScanSource {
    # OfflineRegistryScanSource.from_dict(payload)
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)] $Payload)

    $spec = [Engine]::Get($Payload, 'registry_scan', $null)
    if (-not [Engine]::IsMapping($spec)) {
        throw (Get-DbEngineError 'InvalidDataException' 'registry_scan source requires an object payload')
    }

    $tokenRaw = [Engine]::Get($spec, 'token', $null)
    if (-not [Engine]::Truthy($tokenRaw) -or [EngineText]::Strip([Engine]::Str($tokenRaw)).Length -eq 0) {
        throw (Get-DbEngineError 'InvalidDataException' "registry_scan requires non-empty 'token'.")
    }

    $alias = [Engine]::Get($Payload, 'alias', $null)
    if ($null -ne $alias -and [EngineText]::Strip([Engine]::Str($alias)).Length -eq 0) {
        $alias = $null
    }

    $remoteSpec = [Engine]::Get($spec, 'remote', $null)
    $batchSpec = [Engine]::Or([Engine]::Or([Engine]::Or([Engine]::Get($spec, 'remote_batch', $null), [Engine]::Get($spec, 'remoteTargets', $null)),
            [Engine]::Get($spec, 'remote_targets', $null)), [Engine]::Get($spec, 'batch', $null))

    $remote = $null
    if ($null -ne $remoteSpec) {
        $remote = ConvertFrom-DbRemoteRegistryTarget $remoteSpec
    }

    $batch = [System.Collections.Generic.List[object]]::new()
    if ($null -ne $batchSpec) {
        if ([Engine]::IsMapping($batchSpec)) {
            $batch.Add((ConvertFrom-DbRemoteRegistryTarget $batchSpec))
        }
        elseif ([Engine]::IsList($batchSpec)) {
            foreach ($entry in [Engine]::Iterate($batchSpec)) {
                $batch.Add((ConvertFrom-DbRemoteRegistryTarget $entry))
            }
        }
        else {
            $batch.Add((ConvertFrom-DbRemoteRegistryTarget $batchSpec))
        }
    }

    $token = [EngineText]::Strip([Engine]::Str($tokenRaw))
    $keywords = Get-DbNormalisedSequence ([Engine]::Get($spec, 'keywords', $null))
    $patterns = Get-DbNormalisedSequence ([Engine]::Get($spec, 'patterns', $null))
    $maxDepth = [Engine]::Int([Engine]::Get($spec, 'max_depth', 12))
    $maxHits = [Engine]::Int([Engine]::Get($spec, 'max_hits', 200))
    $timeBudget = [Engine]::Float([Engine]::Get($spec, 'time_budget_s', 10.0))
    $roots = ConvertTo-DbRegistryRootList ([Engine]::Get($spec, 'roots', $null))

    return [pscustomobject]@{
        kind          = 'registry_scan'
        token         = $token
        keywords      = $keywords
        patterns      = $patterns
        max_depth     = $maxDepth
        max_hits      = $maxHits
        time_budget_s = $timeBudget
        alias         = $(if ([Engine]::Truthy($alias)) { [Engine]::Str($alias) } else { $null })
        remote        = $remote
        remote_batch  = $batch.ToArray()
        roots         = $roots
    }
}

function ConvertTo-DbSnapshotColumnMap {
    # offline_runner._normalise_snapshot_columns(value): table -> string[] in first-seen order.
    [CmdletBinding()]
    param($Value)

    $normalised = Get-DbOrderedMap
    if (-not [Engine]::Truthy($Value)) {
        return , $normalised
    }

    if ([Engine]::IsMapping($Value)) {
        foreach ($table in @($Value.Keys)) {
            if (-not [Engine]::Truthy($table)) {
                continue
            }

            $columns = $Value[$table]
            $entries = [System.Collections.Generic.List[string]]::new()
            if ([Engine]::IsSequence($columns)) {
                foreach ($column in [Engine]::Iterate($columns)) {
                    $text = [EngineText]::Strip([Engine]::Str($column))
                    if ($text.Length -gt 0) {
                        $entries.Add($text)
                    }
                }
            }
            else {
                $entries.Add([EngineText]::Strip([Engine]::Str($columns)))
            }

            if ($entries.Count -gt 0) {
                $normalised[[Engine]::Str($table)] = $entries.ToArray()
            }
        }

        return , $normalised
    }

    if ([Engine]::IsSequence($Value)) {
        $grouped = Get-DbOrderedMap
        foreach ($entry in [Engine]::Iterate($Value)) {
            if (-not [Engine]::Truthy($entry)) {
                continue
            }

            $text = [EngineText]::Strip([Engine]::Str($entry))
            $dot = $text.IndexOf('.')
            if ($text.Length -eq 0 -or $dot -lt 0) {
                continue
            }

            $table = [EngineText]::Strip($text.Substring(0, $dot))
            $column = [EngineText]::Strip($text.Substring($dot + 1))
            if ($table.Length -eq 0 -or $column.Length -eq 0) {
                continue
            }

            if (-not $grouped.Contains($table)) {
                $grouped[$table] = [System.Collections.Generic.List[string]]::new()
            }

            $grouped[$table].Add($column)
        }

        foreach ($table in @($grouped.Keys)) {
            $normalised[$table] = $grouped[$table].ToArray()
        }
    }

    return , $normalised
}

function ConvertFrom-DbOfflineSqlSnapshotSource {
    # OfflineSqlSnapshotSource.from_dict(payload)
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)] $Payload)

    $spec = [Engine]::Get($Payload, 'sql_snapshot', $null)
    if (-not [Engine]::IsMapping($spec)) {
        throw (Get-DbEngineError 'InvalidDataException' 'sql_snapshot source requires an object payload')
    }

    $pathValue = [Engine]::Or([Engine]::Get($spec, 'path', $null), [Engine]::Get($Payload, 'path', $null))
    if (-not [Engine]::Truthy($pathValue) -or [EngineText]::Strip([Engine]::Str($pathValue)).Length -eq 0) {
        throw (Get-DbEngineError 'InvalidDataException' "sql_snapshot requires a 'path'.")
    }

    $alias = Get-DbStrippedTruthy ([Engine]::Or([Engine]::Get($Payload, 'alias', $null), [Engine]::Get($spec, 'alias', $null)))
    $optional = [Engine]::Truthy([Engine]::Get($Payload, 'optional', [Engine]::Get($spec, 'optional', $false)))

    $tuples = @{}
    foreach ($key in @('tables', 'exclude_tables')) {
        $raw = [Engine]::Get($spec, $key, $null)
        if (-not [Engine]::Truthy($raw)) {
            $tuples[$key] = @()
        }
        elseif ($raw -is [string]) {
            $tuples[$key] = @([string]$raw)
        }
        else {
            $items = [System.Collections.Generic.List[string]]::new()
            foreach ($item in [Engine]::Iterate($raw)) {
                $text = [EngineText]::Strip([Engine]::Str($item))
                if ($text.Length -gt 0) {
                    $items.Add($text)
                }
            }

            $tuples[$key] = $items.ToArray()
        }
    }

    $maskColumns = ConvertTo-DbSnapshotColumnMap ([Engine]::Get($spec, 'mask_columns', $null))
    $hashColumns = ConvertTo-DbSnapshotColumnMap ([Engine]::Get($spec, 'hash_columns', $null))

    $limitValue = [Engine]::Get($spec, 'limit', $null)
    $limit = $null
    if ($null -ne $limitValue) {
        $limit = [Engine]::Int($limitValue)
        if ($limit.Sign -le 0) {
            throw (Get-DbEngineError 'InvalidDataException' 'sql_snapshot limit must be positive if provided')
        }
    }

    $placeholder = [Engine]::Str([Engine]::Or([Engine]::Or([Engine]::Get($spec, 'placeholder', $null), [Engine]::Get($Payload, 'placeholder', $null)), '[REDACTED]'))
    $hashSalt = [Engine]::Str([Engine]::Or([Engine]::Or([Engine]::Get($spec, 'hash_salt', $null), [Engine]::Get($Payload, 'hash_salt', $null)), ''))
    $dialect = [EngineText]::Lower([Engine]::Str([Engine]::Or([Engine]::Get($spec, 'dialect', $null), 'sqlite')))
    if ($dialect -cne 'sqlite') {
        throw (Get-DbEngineError 'InvalidDataException' "sql_snapshot currently supports only the 'sqlite' dialect")
    }

    return [pscustomobject]@{
        kind           = 'sql_snapshot'
        path           = [Engine]::Str($pathValue)
        alias          = $alias
        optional       = $optional
        tables         = $tuples['tables']
        exclude_tables = $tuples['exclude_tables']
        mask_columns   = $maskColumns
        hash_columns   = $hashColumns
        limit          = $limit
        placeholder    = $placeholder
        hash_salt      = $hashSalt
        dialect        = $dialect
    }
}

function Get-DbDestinationName {
    # source.destination_name(fallback_index=index) for each source kind.
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] $Source,
        [Parameter(Mandatory = $true)][int] $FallbackIndex
    )

    $index = $(if ($FallbackIndex -ge 0) { $FallbackIndex.ToString('00', [System.Globalization.CultureInfo]::InvariantCulture) } else { [string]$FallbackIndex })
    if ($Source.alias) {
        return [EngineText]::SafeName($Source.alias)
    }

    switch ($Source.kind) {
        'registry_scan' {
            $base = $(if ($Source.token) { $Source.token } else { "registry_$index" })
            return [EngineText]::SafeName("registry_$base")
        }
        'sql_snapshot' {
            $stem = [EnginePath]::Stem($Source.path)
            if ($stem) {
                return [EngineText]::SafeName($stem)
            }

            return "sql_snapshot_$index"
        }
        default {
            $name = [EnginePath]::Name((Get-DbExpandedPath $Source.path))
            if ($name) {
                return [EngineText]::SafeName($name)
            }

            return "source_$index"
        }
    }
}

function Get-DbSnapshotArgument {
    # source.snapshot_kwargs()
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)] $Source)

    return [ordered]@{
        tables         = $(if ($Source.tables.Count -gt 0) { $Source.tables } else { $null })
        exclude_tables = $(if ($Source.exclude_tables.Count -gt 0) { $Source.exclude_tables } else { $null })
        mask_columns   = $Source.mask_columns
        hash_columns   = $Source.hash_columns
        limit          = $Source.limit
        placeholder    = $Source.placeholder
        hash_salt      = $Source.hash_salt
    }
}

function ConvertFrom-DbOfflineRunnerProfile {
    # OfflineRunnerProfile.from_dict(payload)
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)] $Payload)

    $name = [Engine]::Get($Payload, 'name', $null)
    if (-not [Engine]::Truthy($name) -or [EngineText]::Strip([Engine]::Str($name)).Length -eq 0) {
        throw (Get-DbEngineError 'InvalidDataException' "Profile requires a non-empty 'name'.")
    }

    $rawSources = [Engine]::Get($Payload, 'sources', $null)
    if (-not [Engine]::Truthy($rawSources)) {
        throw (Get-DbEngineError 'InvalidDataException' 'Profile must define at least one source.')
    }

    $sources = [System.Collections.Generic.List[object]]::new()
    foreach ($entry in [Engine]::Iterate($rawSources)) {
        if ([Engine]::IsMapping($entry)) {
            if ($entry.Contains('registry_scan')) {
                $sources.Add((ConvertFrom-DbOfflineRegistryScanSource $entry))
            }
            elseif ($entry.Contains('sql_snapshot')) {
                $sources.Add((ConvertFrom-DbOfflineSqlSnapshotSource $entry))
            }
            else {
                $sources.Add((ConvertFrom-DbOfflineCollectionSource $entry))
            }
        }
        else {
            $wrapped = Get-DbOrderedMap
            $wrapped['path'] = [Engine]::Str($entry)
            $sources.Add((ConvertFrom-DbOfflineCollectionSource $wrapped))
        }
    }

    $baseline = [Engine]::Get($Payload, 'baseline', $null)
    if ($null -ne $baseline) {
        $baseline = [Engine]::Str($baseline)
        $paths = @($sources | Where-Object { $_.kind -ne 'registry_scan' } | ForEach-Object { $_.path })
        if ($paths -cnotcontains $baseline) {
            throw (Get-DbEngineError 'InvalidDataException' 'Profile baseline must reference one of the declared sources.')
        }
    }

    $tagsPayload = [Engine]::Get($Payload, 'tags', $null)
    if ($tagsPayload -is [string]) {
        $tags = @([string]$tagsPayload)
    }
    elseif ([Engine]::Truthy($tagsPayload)) {
        $tags = Get-DbStringTuple $tagsPayload
    }
    else {
        $tags = @()
    }

    $optionsPayload = [Engine]::Get($Payload, 'options', (Get-DbOrderedMap))
    if (-not [Engine]::IsMapping($optionsPayload)) {
        throw (Get-DbEngineError 'InvalidDataException' "Profile 'options' must be a mapping if provided.")
    }

    $options = Get-DbOrderedMap
    foreach ($key in @($optionsPayload.Keys)) {
        $options[[Engine]::Str($key)] = $optionsPayload[$key]
    }

    $scannerPayload = [Engine]::Get($Payload, 'secret_scanner', (Get-DbOrderedMap))
    if ([Engine]::Truthy($scannerPayload) -and -not [Engine]::IsMapping($scannerPayload)) {
        throw (Get-DbEngineError 'InvalidDataException' "Profile 'secret_scanner' must be a mapping if provided.")
    }

    $scanner = Get-DbOrderedMap
    if ([Engine]::IsMapping($scannerPayload)) {
        foreach ($key in @($scannerPayload.Keys)) {
            $scanner[[Engine]::Str($key)] = $scannerPayload[$key]
        }
    }

    $description = [Engine]::Get($Payload, 'description', $null)
    if ($null -ne $description) {
        $description = [Engine]::Str($description)
    }

    return [pscustomobject]@{
        name           = [Engine]::Str($name)
        description    = $description
        sources        = $sources.ToArray()
        baseline       = $baseline
        tags           = $tags
        options        = $options
        secret_scanner = $scanner
    }
}

function ConvertFrom-DbOfflineEncryptionSetting {
    # OfflineEncryptionSettings.from_dict(payload)
    [CmdletBinding()]
    param($Payload)

    if (-not [Engine]::Truthy($Payload)) {
        return [pscustomobject]@{ enabled = $false; mode = 'dpapi-aes'; keyset_path = $null; output_extension = '.enc'; remove_plaintext = $true }
    }

    if (-not [Engine]::IsMapping($Payload)) {
        throw (Get-DbEngineError 'InvalidDataException' "Runner 'encryption' must be a mapping if provided.")
    }

    $mode = [Engine]::Str([Engine]::Get($Payload, 'mode', 'dpapi-aes'))
    $enabled = [Engine]::Truthy([Engine]::Get($Payload, 'enabled', $true))
    $keysetPath = $null
    $keysetValue = [Engine]::Or([Engine]::Get($Payload, 'keyset_path', $null), [Engine]::Get($Payload, 'keyset', $null))
    if ([Engine]::Truthy($keysetValue)) {
        $keysetPath = Get-DbExpandedPath ([Engine]::Str($keysetValue))
    }

    $outputExtension = [Engine]::Str([Engine]::Get($Payload, 'output_extension', '.enc'))
    if ($outputExtension.Length -gt 0 -and -not $outputExtension.StartsWith('.', [System.StringComparison]::Ordinal)) {
        $outputExtension = ".$outputExtension"
    }

    $removePlaintext = [Engine]::Truthy([Engine]::Get($Payload, 'remove_plaintext', $true))
    $normalisedMode = [EngineText]::Lower([EngineText]::Strip($mode))
    $settings = [pscustomobject]@{
        enabled          = $enabled
        mode             = $(if ($normalisedMode.Length -gt 0) { $normalisedMode } else { 'dpapi-aes' })
        keyset_path      = $keysetPath
        output_extension = $(if ($outputExtension.Length -gt 0) { $outputExtension } else { '.enc' })
        remove_plaintext = $removePlaintext
    }

    if ($settings.enabled -and $null -eq $settings.keyset_path) {
        throw (Get-DbEngineError 'InvalidDataException' "Encryption is enabled but no 'keyset_path' was provided.")
    }

    return $settings
}

function ConvertFrom-DbOfflineRunnerSetting {
    # OfflineRunnerSettings.from_dict(payload)
    [CmdletBinding()]
    param($Payload)

    $defaults = [pscustomobject]@{
        output_directory    = $null
        package_name        = $null
        compress            = $true
        include_config      = $true
        include_logs        = $true
        include_manifest    = $true
        manifest_name       = 'manifest.json'
        log_name            = 'runner.log'
        data_directory_name = 'data'
        logs_directory_name = 'logs'
        max_total_bytes     = $null
        cleanup_staging     = $true
        encryption          = $null
    }

    if (-not [Engine]::Truthy($Payload)) {
        return $defaults
    }

    $directory = [Engine]::Get($Payload, 'output_directory', $null)
    $outputDirectory = $null
    if ([Engine]::Truthy($directory)) {
        if ($directory -isnot [string]) {
            throw (Get-DbEngineError 'InvalidDataException' "output_directory must be a path string, not '$([Engine]::TypeName($directory))'")
        }

        $outputDirectory = [EnginePath]::PathExpandUser([EnginePath]::Normalise([EnginePath]::ExpandVars($directory)))
    }

    $packageName = [Engine]::Get($Payload, 'package_name', $null)
    if ($null -ne $packageName -and [EngineText]::Strip([Engine]::Str($packageName)).Length -eq 0) {
        $packageName = $null
    }

    $manifestName = [Engine]::Get($Payload, 'manifest_name', 'manifest.json')
    $logName = [Engine]::Get($Payload, 'log_name', 'runner.log')
    $dataDirectoryName = [Engine]::Get($Payload, 'data_directory_name', 'data')
    $logsDirectoryName = [Engine]::Get($Payload, 'logs_directory_name', 'logs')

    $maxTotalBytes = [Engine]::Get($Payload, 'max_total_bytes', $null)
    if ($null -ne $maxTotalBytes) {
        $maxTotalBytes = [Engine]::Int($maxTotalBytes)
        if ($maxTotalBytes.Sign -le 0) {
            throw (Get-DbEngineError 'InvalidDataException' 'max_total_bytes must be positive if provided.')
        }
    }

    $encryptionPayload = [Engine]::Get($Payload, 'encryption', $null)
    $encryption = $null
    if ([Engine]::Truthy($encryptionPayload)) {
        $encryption = ConvertFrom-DbOfflineEncryptionSetting $encryptionPayload
    }

    return [pscustomobject]@{
        output_directory    = $outputDirectory
        package_name        = $(if ([Engine]::Truthy($packageName)) { [Engine]::Str($packageName) } else { $null })
        compress            = [Engine]::Truthy([Engine]::Get($Payload, 'compress', $true))
        include_config      = [Engine]::Truthy([Engine]::Get($Payload, 'include_config', $true))
        include_logs        = [Engine]::Truthy([Engine]::Get($Payload, 'include_logs', $true))
        include_manifest    = [Engine]::Truthy([Engine]::Get($Payload, 'include_manifest', $true))
        manifest_name       = [Engine]::Str($manifestName)
        log_name            = [Engine]::Str($logName)
        data_directory_name = [Engine]::Str($dataDirectoryName)
        logs_directory_name = [Engine]::Str($logsDirectoryName)
        max_total_bytes     = $maxTotalBytes
        cleanup_staging     = [Engine]::Truthy([Engine]::Get($Payload, 'cleanup_staging', $true))
        encryption          = $encryption
    }
}

function ConvertFrom-DbOfflineRunnerConfig {
    # OfflineRunnerConfig.from_dict(payload)
    [CmdletBinding()]
    param($Payload)

    if (-not [Engine]::IsMapping($Payload)) {
        throw (Get-DbEngineError 'InvalidDataException' 'Config payload must be a mapping.')
    }

    $schema = [Engine]::Str([Engine]::Get($Payload, 'schema', 'https://driftbuster.dev/offline-runner/config/v1'))
    $version = [Engine]::Str([Engine]::Get($Payload, 'version', '1'))
    $profilePayload = [Engine]::Get($Payload, 'profile', $null)
    if (-not [Engine]::IsMapping($profilePayload)) {
        throw (Get-DbEngineError 'InvalidDataException' "Config requires a 'profile' object.")
    }

    $settingsPayload = [Engine]::Or([Engine]::Get($Payload, 'runner', $null), [Engine]::Get($Payload, 'settings', $null))
    $metadataPayload = [Engine]::Get($Payload, 'metadata', (Get-DbOrderedMap))
    if ([Engine]::Truthy($metadataPayload) -and -not [Engine]::IsMapping($metadataPayload)) {
        throw (Get-DbEngineError 'InvalidDataException' 'Metadata must be a mapping if provided.')
    }

    $profileObject = ConvertFrom-DbOfflineRunnerProfile $profilePayload
    $settings = ConvertFrom-DbOfflineRunnerSetting $settingsPayload

    # dict(metadata_payload): a falsy str or list gives {}, a falsy number, bool or None is refused.
    $metadata = Get-DbOrderedMap
    if ([Engine]::IsMapping($metadataPayload)) {
        foreach ($key in @($metadataPayload.Keys)) {
            $metadata[$key] = $metadataPayload[$key]
        }
    }
    elseif (-not ($metadataPayload -is [string] -or [Engine]::IsList($metadataPayload))) {
        throw (Get-DbEngineError 'InvalidDataException' "A value of type '$([Engine]::TypeName($metadataPayload))' cannot be enumerated.")
    }

    return [pscustomobject]@{
        profile  = $profileObject
        settings = $settings
        metadata = $metadata
        schema   = $schema
        version  = $version
        raw      = $Payload
    }
}

function Get-DbDefaultPackageName {
    # config.default_package_name(timestamp=timestamp)
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] $Config,
        [string] $Timestamp
    )

    $stamp = $(if ($Timestamp) { $Timestamp } else { Get-DbTimestamp })
    return '{0}-{1}' -f [EngineText]::SafeName($Config.profile.name), $stamp
}

function Import-DbOfflineRunnerConfig {
    # load_config(path)
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string] $Path)

    $payload = [EngineJson]::LoadsFile($Path)
    return ConvertFrom-DbOfflineRunnerConfig $payload
}

# secret_scanning as offline_runner uses it: the ruleset (inline in the config, else the packaged rules file), the ignore lists,
# the manifest summary and the scrubbing copy.

function ConvertTo-DbCompiledRuleset {
    # _compile_ruleset_from_mapping(payload): $null when nothing compiles.
    [CmdletBinding()]
    param($Payload)

    return [SecretScanner]::CompileRuleset($Payload)
}

function Get-DbSecretOptionValue {
    # secret_option_values(value)
    [CmdletBinding()]
    param($Value)

    return , ([SecretScanner]::OptionValues($Value).ToArray())
}

function Get-DbSecretRuleFile {
    # Rule files that override the embedded rules: secret_rules.json beside the runner, then the repository's backend resource.
    [CmdletBinding()]
    param()

    return @(
        (Join-Path -Path $PSScriptRoot -ChildPath 'secret_rules.json'),
        [System.IO.Path]::Combine((Split-Path -Path $PSScriptRoot -Parent), 'gui', 'DriftBuster.Backend', 'Resources', 'secret_rules.json')
    )
}

function Get-DbEmbeddedSecretRuleText {
    # gui/DriftBuster.Backend/Resources/secret_rules.json, verbatim; scripts/lint_powershell.ps1 fails when the two differ.
    [CmdletBinding()]
    [OutputType([string])]
    param()

    return @'
{
  "version": "2024-06-01",
  "rules": [
    {
      "name": "PasswordAssignment",
      "description": "Matches common password assignment patterns in configuration files.",
      "pattern": "(?i)password\\s*[:=]\\s*['\"]?[A-Za-z0-9\\-_/+=]{8,}",
      "flags": ""
    },
    {
      "name": "GenericApiToken",
      "description": "Detects API key or token style strings with obvious labels.",
      "pattern": "(?i)(api|auth|token)[-_ ]?(key|token)\\s*[:=]\\s*['\"]?[A-Za-z0-9]{16,}",
      "flags": ""
    },
    {
      "name": "AwsAccessKeyId",
      "description": "AWS-style access key identifiers.",
      "pattern": "AKIA[0-9A-Z]{16}",
      "flags": ""
    }
  ]
}
'@
}

function Get-DbPackagedSecretRule {
    # load_secret_rules(): (rules, version, loaded), read once per session.
    [CmdletBinding()]
    param()

    if ($null -ne [SecretScanner]::PackagedRules) {
        return [SecretScanner]::PackagedRules
    }

    $payload = $null
    foreach ($candidate in Get-DbSecretRuleFile) {
        if ([System.IO.File]::Exists($candidate)) {
            $payload = [EngineJson]::LoadsFile($candidate)
            break
        }
    }

    if ($null -eq $payload) {
        # The runner ships as one file, so the default rules travel inside it.
        $payload = [EngineJson]::Loads((Get-DbEmbeddedSecretRuleText))
    }

    $compiled = [SecretScanner]::CompileRuleset($payload)
    if ($null -eq $compiled) {
        [SecretScanner]::PackagedRules = [pscustomobject]@{ ruleset = $null; version = [Engine]::Str([Engine]::Get($payload, 'version', 'unknown')); loaded = $true }
        return [SecretScanner]::PackagedRules
    }

    $version = $(if ($compiled.Version) { $compiled.Version } else { [Engine]::Str([Engine]::Get($payload, 'version', 'unknown')) })
    [SecretScanner]::PackagedRules = [pscustomobject]@{ ruleset = $compiled; version = $version; loaded = $true }
    return [SecretScanner]::PackagedRules
}

function Get-DbSecretContext {
    # build_context(options, secret_scanner)
    [CmdletBinding()]
    param($Options, $SecretScanner)

    $rulesetPayload = $null
    if ([Engine]::Truthy($SecretScanner) -and [Engine]::IsMapping($SecretScanner)) {
        $rulesetPayload = [Engine]::Get($SecretScanner, 'ruleset', $null)
    }

    $compiled = $null
    if ([Engine]::IsMapping($rulesetPayload)) {
        $compiled = [SecretScanner]::CompileRuleset($rulesetPayload)
    }

    if ($null -ne $compiled) {
        return [SecretScanner]::BuildContext($Options, $SecretScanner, $compiled, $compiled.Rules.Count -gt 0)
    }

    $packaged = Get-DbPackagedSecretRule
    $context = [SecretScanner]::BuildContext($Options, $SecretScanner, $packaged.ruleset, $packaged.loaded)
    $context.Version = $packaged.version
    return $context
}

function Get-DbManifestSecretScanner {
    # manifest_secret_scanner(options, secret_scanner, context)
    [CmdletBinding()]
    param($Options, $SecretScanner, [Parameter(Mandatory = $true)] $Context)

    $ignoreRules = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    $ignorePatterns = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    foreach ($value in [SecretScanner]::OptionValues([Engine]::Get($Options, 'secret_ignore_rules', $null))) { [void]$ignoreRules.Add($value) }
    foreach ($value in [SecretScanner]::OptionValues([Engine]::Get($SecretScanner, 'ignore_rules', $null))) { [void]$ignoreRules.Add($value) }
    foreach ($value in [SecretScanner]::OptionValues([Engine]::Get($Options, 'secret_ignore_patterns', $null))) { [void]$ignorePatterns.Add($value) }
    foreach ($value in [SecretScanner]::OptionValues([Engine]::Get($SecretScanner, 'ignore_patterns', $null))) { [void]$ignorePatterns.Add($value) }

    $sortedRules = [System.Collections.Generic.List[string]]::new($ignoreRules)
    $sortedRules.Sort([System.Comparison[string]] { param($left, $right) [EngineText]::CompareCodePoints($left, $right) })
    $sortedPatterns = [System.Collections.Generic.List[string]]::new($ignorePatterns)
    $sortedPatterns.Sort([System.Comparison[string]] { param($left, $right) [EngineText]::CompareCodePoints($left, $right) })

    $manifest = Get-DbOrderedMap
    $manifest['ignore_rules'] = $sortedRules
    $manifest['ignore_patterns'] = $sortedPatterns
    $manifest['ruleset_version'] = $Context.Version
    return , $manifest
}

function Copy-DbFileWithSecretFilter {
    # copy_with_secret_filter(source, destination, display_path=..., context=..., log=...): the destination size and SHA-256.
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string] $Source,
        [Parameter(Mandatory = $true)][string] $Destination,
        [Parameter(Mandatory = $true)][string] $DisplayPath,
        [Parameter(Mandatory = $true)] $Context,
        [Parameter(Mandatory = $true)] $Log
    )

    return [SecretScanner]::CopyWithSecretFilter($Source, $Destination, $DisplayPath, $Context, $Log)
}

# File and glob sources: matching a source path, applying excludes, and the collection loop a config runs.

# One wildcard syntax, shared with the backend (PathWildcard): * matches any run of characters (a / included), ? matches one
# character, and every other character (brackets, backtick and backslash included) is literal. Case-insensitive on Windows,
# case-sensitive elsewhere.

function Test-DbPathMagic {
    # The text holds a wildcard character: * or ?.
    [CmdletBinding()]
    param([AllowEmptyString()][string] $Text)

    return $Text.IndexOfAny([char[]]@('*', '?')) -ge 0
}

function Test-DbWildcardMatch {
    # The whole text matches the pattern. On Windows \ reads as / in both. An empty pattern never matches, and nothing matches empty text.
    [CmdletBinding()]
    param(
        [AllowEmptyString()][string] $Text,
        [AllowEmptyString()][string] $Pattern
    )

    if ($Pattern.Length -eq 0 -or $Text.Length -eq 0) {
        return $false
    }

    $options = [System.Management.Automation.WildcardOptions]::None
    if ([EngineOs]::Windows) {
        $Text = $Text.Replace('\', '/')
        $Pattern = $Pattern.Replace('\', '/')
        $options = [System.Management.Automation.WildcardOptions]::IgnoreCase
    }

    $escaped = $Pattern.Replace('`', '``').Replace('[', '`[').Replace(']', '`]')
    return [System.Management.Automation.WildcardPattern]::new($escaped, $options).IsMatch($Text)
}

function Get-DbDirectoryEntry {
    # The entries of a directory, hidden and system ones included; none when it cannot be listed.
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string] $Directory)

    try {
        return [System.IO.DirectoryInfo]::new($Directory).GetFileSystemInfos()
    }
    catch {
        return @()
    }
}

function Join-DbGlobPath {
    # A child name under a directory; the name alone under the working directory (an empty directory text).
    [CmdletBinding()]
    param(
        [AllowEmptyString()][string] $Directory,
        [Parameter(Mandatory = $true)][string] $Name
    )

    if ($Directory.Length -eq 0) {
        return $Name
    }

    return [System.IO.Path]::Combine($Directory, $Name)
}

function Test-DbWalkableDirectory {
    # A directory that is not a symbolic link or junction: the glob walk descends only into these.
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)] $Entry)

    $attributes = $Entry.Attributes
    return (($attributes -band [System.IO.FileAttributes]::Directory) -ne 0) -and (($attributes -band [System.IO.FileAttributes]::ReparsePoint) -eq 0)
}

function Add-DbGlobMatch {
    # Adds to Result the paths below Directory that Segment[Index..] match: a segment without wildcards names an entry, ** stands for
    # zero or more directory levels, and any other segment is matched against entry names. Links are returned, never descended into.
    [CmdletBinding()]
    param(
        [AllowEmptyString()][string] $Directory,
        [Parameter(Mandatory = $true)][string[]] $Segment,
        [Parameter(Mandatory = $true)][int] $Index,
        [Parameter(Mandatory = $true)] $Result
    )

    $current = $Segment[$Index]
    $last = $Index -eq ($Segment.Count - 1)
    $listing = $(if ($Directory.Length -eq 0) { '.' } else { $Directory })
    if ($current -ceq '**') {
        if (-not $last) {
            Add-DbGlobMatch -Directory $Directory -Segment $Segment -Index ($Index + 1) -Result $Result
        }
        elseif ($Directory.Length -gt 0) {
            [void]$Result.Add($Directory)
        }

        foreach ($entry in @(Get-DbDirectoryEntry -Directory $listing)) {
            if (Test-DbWalkableDirectory -Entry $entry) {
                Add-DbGlobMatch -Directory (Join-DbGlobPath -Directory $Directory -Name $entry.Name) -Segment $Segment -Index $Index -Result $Result
            }
        }

        return
    }

    if (-not (Test-DbPathMagic $current)) {
        $child = Join-DbGlobPath -Directory $Directory -Name $current
        if ($last) {
            if ([EngineFs]::Exists($child) -or [EngineFs]::IsSymlink($child)) {
                [void]$Result.Add($child)
            }
        }
        elseif ([System.IO.Directory]::Exists($child)) {
            Add-DbGlobMatch -Directory $child -Segment $Segment -Index ($Index + 1) -Result $Result
        }

        return
    }

    foreach ($entry in @(Get-DbDirectoryEntry -Directory $listing)) {
        if (-not (Test-DbWildcardMatch -Text $entry.Name -Pattern $current)) {
            continue
        }

        $child = Join-DbGlobPath -Directory $Directory -Name $entry.Name
        if ($last) {
            [void]$Result.Add($child)
        }
        elseif (Test-DbWalkableDirectory -Entry $entry) {
            Add-DbGlobMatch -Directory $child -Segment $Segment -Index ($Index + 1) -Result $Result
        }
    }
}

function Get-DbGlobMatch {
    # The distinct paths a pattern carrying its own base directory matches, in no particular order: the root and the leading segments
    # without wildcards name the directory the rest is matched under (the working directory when there are none).
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string] $Pattern)

    $drive = $null
    $root = $null
    $tail = $null
    [EnginePath]::SplitRoot($Pattern, [ref]$drive, [ref]$root, [ref]$tail)
    $separators = $(if ([EngineOs]::Windows) { [char[]]@('\', '/') } else { [char[]]@('/') })
    $segments = [string[]]@($tail.Split($separators, [System.StringSplitOptions]::RemoveEmptyEntries) | Where-Object { $_ -cne '.' })
    $result = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    $literal = 0
    while ($literal -lt $segments.Count -and -not (Test-DbPathMagic $segments[$literal])) {
        $literal++
    }

    if ($literal -eq $segments.Count) {
        if ([EngineFs]::Exists($Pattern) -or [EngineFs]::IsSymlink($Pattern)) {
            [void]$result.Add($Pattern)
        }
    }
    else {
        $base = $drive + $root
        if ($literal -gt 0) {
            $base += [string]::Join([EngineOs]::Sep, $segments, 0, $literal)
        }

        if ([System.IO.Directory]::Exists($(if ($base.Length -eq 0) { '.' } else { $base }))) {
            Add-DbGlobMatch -Directory $base -Segment $segments -Index $literal -Result $result
        }
    }

    return , [System.Collections.Generic.List[string]]::new($result)
}

function Get-DbSourceMatch {
    # The paths a source matches, relative to the base directory; FileNotFoundException when there are none.
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string] $PathText,
        [AllowNull()][string] $BaseDir
    )

    $expandedText = [EnginePath]::ExpandUser([EnginePath]::ExpandVars($PathText))
    $patterns = [System.Collections.Generic.List[string]]::new()
    if ($BaseDir -and -not [EnginePath]::IsAbsolute($expandedText)) {
        $patterns.Add([EnginePath]::Join($BaseDir, $expandedText))
    }

    if (-not $patterns.Contains($expandedText)) {
        $patterns.Add($expandedText)
    }

    $found = [System.Collections.Generic.List[string]]::new()
    if (-not (Test-DbPathMagic $PathText)) {
        foreach ($pattern in $patterns) {
            if ([EngineFs]::Exists($pattern)) {
                $found.Add([EnginePath]::Normalise($pattern))
                return , $found.ToArray()
            }
        }

        throw (Get-DbEngineError 'FileNotFoundException' "Path does not exist: $PathText")
    }

    $seen = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    $matched = $false
    foreach ($pattern in $patterns) {
        $globbed = Get-DbGlobMatch -Pattern $pattern
        [EnginePath]::SortByText($globbed)
        foreach ($match in $globbed) {
            $matched = $true
            if ($seen.Add($match)) {
                $found.Add([EnginePath]::Normalise($match))
            }
        }
    }

    if (-not $matched) {
        throw (Get-DbEngineError 'FileNotFoundException' "Path does not exist: $PathText")
    }

    return , $found.ToArray()
}

function Test-DbExcluded {
    # A pattern matches the relative path (posix form) or its last segment.
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string] $Relative,
        [AllowNull()][AllowEmptyCollection()][string[]] $Pattern
    )

    if ($null -eq $Pattern -or $Pattern.Count -eq 0) {
        return $false
    }

    $text = [EnginePath]::AsPosix($Relative)
    $name = [EnginePath]::Name($Relative)
    foreach ($candidate in $Pattern) {
        if ((Test-DbWildcardMatch -Text $text -Pattern $candidate) -or (Test-DbWildcardMatch -Text $name -Pattern $candidate)) {
            return $true
        }
    }

    return $false
}

function Invoke-DbFileSource {
    # The file branch of a config run: collected files, the source summary and the running byte total.
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] $Source,
        [Parameter(Mandatory = $true)][string] $Alias,
        [Parameter(Mandatory = $true)][string] $DestinationRoot,
        [Parameter(Mandatory = $true)][string] $DataRoot,
        [AllowNull()][string] $BaseDir,
        $MaxTotalBytes,
        [long] $TotalBytes,
        [Parameter(Mandatory = $true)] $SecretContext,
        [Parameter(Mandatory = $true)] $Log
    )

    $files = [System.Collections.Generic.List[object]]::new()
    try {
        $matches_ = Get-DbSourceMatch -PathText $Source.path -BaseDir $BaseDir
    }
    catch {
        $engineError = Get-DbEngineException $_
        if ($null -eq $engineError -or $engineError.ErrorType -cne 'FileNotFoundException') {
            throw
        }

        if ($Source.optional) {
            $Log.Write("optional source skipped: $($Source.path)")
            $summary = Get-DbOrderedMap
            $summary['type'] = 'file'
            $summary['path'] = $Source.path
            $summary['alias'] = $Alias
            $summary['optional'] = $true
            $summary['matched'] = [string[]]@()
            $summary['skipped'] = $true
            $summary['reason'] = $(if (Test-DbPathMagic $Source.path) { 'no-matches' } else { 'missing' })
            $summary['exclude'] = [string[]]@($Source.exclude)
            return [pscustomobject]@{ Files = $files; Summary = $summary; TotalBytes = $TotalBytes }
        }

        $Log.Write("required source missing: $($Source.path)")
        throw
    }

    $ordered = [System.Collections.Generic.List[string]]::new()
    foreach ($match in $matches_) {
        $ordered.Add($match)
    }

    $ordered.Sort([System.Comparison[string]] { param($left, $right) [EngineText]::CompareCodePoints([EnginePath]::AsPosix($left), [EnginePath]::AsPosix($right)) })

    $collected = [System.Collections.Generic.List[string]]::new()
    $processed = [System.Collections.Generic.List[string]]::new()
    $running = $TotalBytes
    foreach ($match in $ordered) {
        if ([EngineFs]::IsSymlink($match)) {
            $Log.Write("skipping symlink: $match")
            continue
        }

        $resolved = [EngineFs]::Realpath($match)
        $within = $false
        foreach ($directory in $processed) {
            if ($null -ne [EnginePath]::RelativeTo($resolved, $directory)) {
                $within = $true
                break
            }
        }

        if ($within) {
            $Log.Write("skipping already collected: $match")
            continue
        }

        $pairs = [System.Collections.Generic.List[object]]::new()
        if ([EngineFs]::IsDir($match)) {
            $processed.Add($resolved)
            $walker = [EngineFs]::RglobFiles($match)
            $walker.Sort([System.Comparison[string]] { param($left, $right) [EngineText]::CompareCodePoints([EnginePath]::AsPosix($left), [EnginePath]::AsPosix($right)) })
            foreach ($file in $walker) {
                $relative = [EnginePath]::RelativeTo($file, $match)
                if ($null -eq $relative) {
                    $relative = [EnginePath]::Name($file)
                }

                $pairs.Add(@($file, $relative))
            }
        }
        elseif ([EngineFs]::IsFile($match)) {
            $pairs.Add(@($match, [EnginePath]::Name($match)))
        }

        foreach ($pair in $pairs) {
            $file = $pair[0]
            $relative = $pair[1]
            if (Test-DbExcluded -Relative $relative -Pattern $Source.exclude) {
                $Log.Write("excluded $file by pattern")
                continue
            }

            $originalSize = [EngineFs]::Size($file)
            if ($null -ne $MaxTotalBytes -and ([System.Numerics.BigInteger]::new($running) + $originalSize) -gt $MaxTotalBytes) {
                throw (Get-DbEngineError 'InvalidOperationException' 'Collection exceeds configured max_total_bytes limit.')
            }

            $destination = [EnginePath]::Join($DestinationRoot, $relative)
            $relativeToData = [EnginePath]::RelativeTo($destination, $DataRoot)
            if ($null -eq $relativeToData) {
                $relativeToData = [EnginePath]::Name($destination)
            }

            $display = [EnginePath]::AsPosix($relativeToData)
            $copy = Copy-DbFileWithSecretFilter -Source $file -Destination $destination -DisplayPath $display -Context $SecretContext -Log $Log
            $running += $copy.Size
            $files.Add([pscustomobject]@{
                    alias         = $Alias
                    source        = $Source.path
                    destination   = $destination
                    relative_path = $display
                    size          = $copy.Size
                    sha256        = $copy.Sha256
                })
            $collected.Add([EnginePath]::AsPosix($relative))
        }
    }

    $summary = Get-DbOrderedMap
    $summary['path'] = $Source.path
    $summary['alias'] = $Alias
    $summary['optional'] = [bool]$Source.optional
    $summary['matched'] = $collected.ToArray()
    $summary['skipped'] = $false
    $summary['exclude'] = [string[]]@($Source.exclude)
    $Log.Write("collected $($collected.Count) items from $($Source.path)")
    return [pscustomobject]@{ Files = $files; Summary = $summary; TotalBytes = $running }
}

# Building a SQLite snapshot, and the sql_snapshot branch of a config run.

function Get-DbSqliteSnapshot {
    # build_sqlite_snapshot(path, **kwargs).to_dict()
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        $Tables,
        $ExcludeTables,
        $MaskColumns,
        $HashColumns,
        $Limit,
        [string] $Placeholder = '[REDACTED]',
        [string] $HashSalt = ''
    )

    $tableList = $null
    if ($null -ne $Tables) {
        $tableList = [System.Collections.ArrayList]@($Tables)
    }

    $excludeList = $null
    if ($null -ne $ExcludeTables) {
        $excludeList = [System.Collections.ArrayList]@($ExcludeTables)
    }
    $maskMap = $(if ($MaskColumns -is [System.Collections.IDictionary]) { $MaskColumns } else { ConvertTo-DbSnapshotColumnMap $MaskColumns })
    $hashMap = $(if ($HashColumns -is [System.Collections.IDictionary]) { $HashColumns } else { ConvertTo-DbSnapshotColumnMap $HashColumns })
    return [SqlSnapshots]::Build([EnginePath]::Normalise($Path), $tableList, $excludeList, $maskMap, $hashMap, $Limit, $Placeholder, $HashSalt)
}

function ConvertTo-DbColumnListMap {
    # {table: list(columns)}
    [CmdletBinding()]
    param($Columns)

    $map = Get-DbOrderedMap
    foreach ($table in @($Columns.Keys)) {
        $map[$table] = [string[]]@($Columns[$table])
    }

    return , $map
}

function Invoke-DbSqlSnapshotSource {
    # The sql_snapshot branch of a config run: the summary, the sql_exports metadata entry and the collected file, or the skipped
    # summary of an optional source whose database is missing.
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] $Source,
        [Parameter(Mandatory = $true)][string] $Alias,
        [Parameter(Mandatory = $true)][string] $DestinationRoot,
        [AllowNull()][string] $BaseDir,
        $MaxTotalBytes,
        [long] $TotalBytes,
        [Parameter(Mandatory = $true)] $Log
    )

    $candidateRaw = Get-DbExpandedPath $Source.path
    $candidate = $candidateRaw
    if ($BaseDir -and -not [EnginePath]::IsAbsolute($candidateRaw)) {
        $candidate = [EnginePath]::PathExpandUser([EnginePath]::Join($BaseDir, $candidateRaw))
    }

    if (-not [EngineFs]::Exists($candidate) -and [EngineFs]::Exists($candidateRaw)) {
        $candidate = $candidateRaw
    }

    if (-not [EngineFs]::Exists($candidate)) {
        if ($Source.optional) {
            $Log.Write("optional sql snapshot skipped: $($Source.path)")
            $skipped = Get-DbOrderedMap
            $skipped['type'] = 'sql_snapshot'
            $skipped['path'] = $Source.path
            $skipped['alias'] = $Alias
            $skipped['optional'] = $true
            $skipped['skipped'] = $true
            $skipped['reason'] = 'missing'
            return [pscustomobject]@{ Summary = $skipped; Metadata = $null; File = $null }
        }

        $Log.Write("sql snapshot source missing: $($Source.path)")
        throw (Get-DbEngineError 'FileNotFoundException' "SQL snapshot source not found: $($Source.path)")
    }

    $Log.Write("building sql snapshot from $candidate")
    $arguments = Get-DbSnapshotArgument $Source
    $payload = Get-DbSqliteSnapshot -Path $candidate -Tables $arguments.tables -ExcludeTables $arguments.exclude_tables `
        -MaskColumns $arguments.mask_columns -HashColumns $arguments.hash_columns -Limit $arguments.limit `
        -Placeholder $arguments.placeholder -HashSalt $arguments.hash_salt
    $encoded = [EngineFile]::EncodeUtf8([EngineJson]::Dumps($payload, 2, $true))
    if ($null -ne $MaxTotalBytes -and ([System.Numerics.BigInteger]::new($TotalBytes) + $encoded.Length) -gt $MaxTotalBytes) {
        throw (Get-DbEngineError 'InvalidOperationException' 'Collection exceeds configured max_total_bytes limit.')
    }

    $snapshotPath = [EnginePath]::Join($DestinationRoot, 'sql-snapshot.json')
    [EngineFile]::WriteBytes($snapshotPath, $encoded)

    $tableNames = [string[]]@($payload['tables'] | ForEach-Object { $_['name'] })
    $rowCounts = Get-DbOrderedMap
    foreach ($table in $payload['tables']) {
        $rowCounts[$table['name']] = $table['row_count']
    }

    $summary = Get-DbOrderedMap
    $summary['type'] = 'sql_snapshot'
    $summary['path'] = $Source.path
    $summary['alias'] = $Alias
    $summary['dialect'] = $Source.dialect
    $summary['tables'] = $tableNames
    $summary['row_counts'] = $rowCounts
    $summary['masked_columns'] = ConvertTo-DbColumnListMap $Source.mask_columns
    $summary['hashed_columns'] = ConvertTo-DbColumnListMap $Source.hash_columns

    $metadata = Get-DbOrderedMap
    $metadata['alias'] = $Alias
    $metadata['source'] = $Source.path
    $metadata['dialect'] = $Source.dialect
    $metadata['tables'] = $tableNames
    $metadata['row_counts'] = $rowCounts
    $metadata['masked_columns'] = ConvertTo-DbColumnListMap $Source.mask_columns
    $metadata['hashed_columns'] = ConvertTo-DbColumnListMap $Source.hash_columns
    $metadata['placeholder'] = $Source.placeholder
    $metadata['hash_salt'] = $Source.hash_salt
    $metadata['output'] = 'sql-snapshot.json'

    $Log.Write("sql snapshot exported with $(@($payload['tables']).Count) table(s)")
    $file = [pscustomobject]@{
        alias         = $Alias
        source        = "sql:$($Source.dialect)"
        destination   = $snapshotPath
        relative_path = 'sql-snapshot.json'
        size          = [long]$encoded.Length
        sha256        = [EngineFile]::HashFile($snapshotPath)
    }

    return [pscustomobject]@{ Summary = $summary; Metadata = $metadata; File = $file }
}

# registry.scan over Microsoft.Win32.RegistryKey: installed application enumeration, root suggestions for a token and the
# breadth-first value search, plus the registry_scan branch of a config run. The two backend functions are the only registry
# calls, so tests replace them.

function Test-DbWindowsPlatform {
    # registry.is_windows()
    [CmdletBinding()]
    param()

    return [EngineOs]::Windows
}

function Open-DbRegistryKey {
    # _WinRegBackend._open: the key read-only in the requested view, or $null where the key cannot be opened.
    [CmdletBinding()]
    param([string] $Hive, [string] $Path, $View)

    switch -CaseSensitive ($Hive) {
        'HKLM' { $baseHive = [Microsoft.Win32.RegistryHive]::LocalMachine }
        'HKCU' { $baseHive = [Microsoft.Win32.RegistryHive]::CurrentUser }
        default { throw (Get-DbEngineError 'KeyNotFoundException' "Unknown registry hive $([EngineText]::Repr($Hive)).") }
    }

    $registryView = [Microsoft.Win32.RegistryView]::Default
    if ($View -ceq '64') {
        $registryView = [Microsoft.Win32.RegistryView]::Registry64
    }
    elseif ($View -ceq '32') {
        $registryView = [Microsoft.Win32.RegistryView]::Registry32
    }

    try {
        $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey($baseHive, $registryView)
        if ($Path.Length -eq 0) {
            return $base
        }

        $key = $base.OpenSubKey($Path, $false)
        $base.Dispose()
        return $key
    }
    catch [System.Security.SecurityException], [System.UnauthorizedAccessException], [System.IO.IOException], [System.ArgumentException] {
        return $null
    }
}

function Get-DbRegistrySubkey {
    # backend.enum_subkeys(hive, path, view)
    [CmdletBinding()]
    param([string] $Hive, [string] $Path, $View)

    $key = Open-DbRegistryKey -Hive $Hive -Path $Path -View $View
    if ($null -eq $key) {
        return , @()
    }

    try {
        return , @($key.GetSubKeyNames())
    }
    catch [System.Security.SecurityException], [System.UnauthorizedAccessException], [System.IO.IOException] {
        return , @()
    }
    finally {
        $key.Dispose()
    }
}

function ConvertFrom-DbRegistryData {
    # The Python value winreg.EnumValue returns for a value read through RegistryKey.
    [CmdletBinding()]
    param($Data, [Microsoft.Win32.RegistryValueKind] $Kind)

    switch ($Kind) {
        'DWord' { return [System.Numerics.BigInteger]::new([uint32][System.BitConverter]::ToUInt32([System.BitConverter]::GetBytes([int]$Data), 0)) }
        'QWord' { return [System.Numerics.BigInteger]::new([System.BitConverter]::ToUInt64([System.BitConverter]::GetBytes([long]$Data), 0)) }
        'String' { return ([string]$Data).Split([char]0)[0] }
        'ExpandString' { return ([string]$Data).Split([char]0)[0] }
        'MultiString' {
            $items = [System.Collections.Generic.List[object]]::new()
            foreach ($item in @($Data)) {
                $items.Add([string]$item)
            }

            return , $items
        }
        default {
            if ($Data -is [byte[]]) {
                if ($Data.Length -eq 0) {
                    return $null
                }

                return , $Data
            }

            return $Data
        }
    }
}

function Get-DbRegistryValue {
    # backend.enum_values(hive, path, view): (name, data) pairs in the key's order.
    [CmdletBinding()]
    param([string] $Hive, [string] $Path, $View)

    $values = [System.Collections.Generic.List[object]]::new()
    $key = Open-DbRegistryKey -Hive $Hive -Path $Path -View $View
    if ($null -eq $key) {
        return , $values
    }

    try {
        foreach ($name in $key.GetValueNames()) {
            try {
                $kind = $key.GetValueKind($name)
                if ($kind -eq [Microsoft.Win32.RegistryValueKind]::Unknown -or $kind -eq [Microsoft.Win32.RegistryValueKind]::None) {
                    # REG_NONE, REG_DWORD_BIG_ENDIAN, REG_LINK, the resource lists and non-standard types: winreg returns their bytes.
                    $rawType = 0
                    $data = [EngineWinreg]::QueryRaw($key.Handle, $name, [ref]$rawType)
                    $kind = [Microsoft.Win32.RegistryValueKind]::Binary
                }
                else {
                    $data = $key.GetValue($name, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
                }
            }
            catch [System.Security.SecurityException], [System.UnauthorizedAccessException], [System.IO.IOException] {
                break
            }

            $values.Add([pscustomobject]@{ Name = $name; Data = (ConvertFrom-DbRegistryData -Data $data -Kind $kind) })
        }
    }
    catch [System.Security.SecurityException], [System.UnauthorizedAccessException], [System.IO.IOException] {
        return , $values
    }
    finally {
        $key.Dispose()
    }

    return , $values
}

function Get-DbRegistryTruthyText {
    [CmdletBinding()]
    param($Values, [string] $Name)

    if ($Values.ContainsKey($Name) -and [Engine]::Truthy($Values[$Name])) {
        return [Engine]::Str($Values[$Name])
    }

    return $null
}

function Get-DbInstalledApp {
    # enumerate_installed_apps()
    [CmdletBinding()]
    param()

    $uninstall = 'Software\Microsoft\Windows\CurrentVersion\Uninstall'
    $uninstallWow = 'Software\Wow6432Node\Microsoft\Windows\CurrentVersion\Uninstall'
    $probes = @(
        @('HKLM', $uninstall, '64'),
        @('HKLM', $uninstallWow, '32'),
        @('HKCU', $uninstall, $null)
    )

    $apps = [System.Collections.Generic.List[object]]::new()
    foreach ($probe in $probes) {
        $hive = $probe[0]
        $base = $probe[1]
        $view = $probe[2]
        foreach ($subkey in (Get-DbRegistrySubkey -Hive $hive -Path $base -View $view)) {
            $keyPath = "$base\$subkey"
            $values = [System.Collections.Generic.Dictionary[string, object]]::new([System.StringComparer]::Ordinal)
            foreach ($pair in (Get-DbRegistryValue -Hive $hive -Path $keyPath -View $view)) {
                $values[$pair.Name] = $pair.Data
            }

            $displayName = [EngineText]::Strip([string](Get-DbRegistryTruthyText $values 'DisplayName'))
            if ($displayName.Length -eq 0) {
                continue
            }

            $apps.Add([pscustomobject]@{
                    display_name     = $displayName
                    key_path         = $keyPath
                    hive             = $hive
                    publisher        = (Get-DbRegistryTruthyText $values 'Publisher')
                    version          = (Get-DbRegistryTruthyText $values 'DisplayVersion')
                    uninstall_string = (Get-DbRegistryTruthyText $values 'UninstallString')
                    install_location = (Get-DbRegistryTruthyText $values 'InstallLocation')
                    view             = $(if ($null -ne $view) { $view } else { 'auto' })
                })
        }
    }

    $seen = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    $unique = [System.Collections.Generic.List[object]]::new()
    foreach ($app in $apps) {
        if ($seen.Add("$($app.hive)`n$($app.key_path)")) {
            $unique.Add($app)
        }
    }

    $sorted = [System.Collections.Generic.List[object]]::new()
    foreach ($app in $unique) {
        $position = $sorted.Count
        while ($position -gt 0) {
            $previous = $sorted[$position - 1]
            $byName = [EngineText]::CompareCodePoints([EngineText]::Lower($previous.display_name), [EngineText]::Lower($app.display_name))
            if ($byName -lt 0 -or ($byName -eq 0 -and [EngineText]::CompareCodePoints($previous.hive, $app.hive) -le 0)) {
                break
            }

            $position--
        }

        $sorted.Insert($position, $app)
    }

    return , $sorted.ToArray()
}

function Get-DbAppRegistryRoot {
    # find_app_registry_roots(app_token, installed=installed): (hive, path, view) roots in order, duplicates dropped.
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string] $Token, $Installed)

    $needle = [EngineText]::Lower([EngineText]::Strip($Token))
    $candidates = [System.Collections.Generic.List[object]]::new()
    foreach ($app in @($Installed)) {
        if ($null -eq $app) {
            continue
        }

        $inName = [EngineText]::Lower($app.display_name).Contains($needle)
        $inPublisher = $app.publisher -and [EngineText]::Lower($app.publisher).Contains($needle)
        if (-not ($inName -or $inPublisher)) {
            continue
        }

        $appView = $(if ($app.view -ceq '32' -or $app.view -ceq '64') { $app.view } else { $null })
        $parts = @([regex]::Split($app.display_name, '[\s_-]+') | Where-Object { $_.Length -gt 0 })
        $pairs = [System.Collections.Generic.List[object]]::new()
        if ($parts.Count -ge 2) {
            $pairs.Add(@($parts[0], (($parts | Select-Object -Skip 1) -join ' ')))
        }

        $pairs.Add(@('', $app.display_name))
        foreach ($pair in $pairs) {
            $segments = @(@([EngineText]::Strip($pair[0]), [EngineText]::Strip($pair[1])) | Where-Object { $_.Length -gt 0 })
            $suffix = $segments -join '\'
            if ($suffix.Length -gt 0) {
                $candidates.Add([pscustomobject]@{ hive = 'HKCU'; path = "Software\$suffix"; view = $null })
                $candidates.Add([pscustomobject]@{ hive = 'HKLM'; path = "Software\$suffix"; view = $appView })
                $candidates.Add([pscustomobject]@{ hive = 'HKLM'; path = "Software\Wow6432Node\$suffix"; view = '32' })
            }
        }

        $candidates.Add([pscustomobject]@{ hive = $app.hive; path = $app.key_path; view = $appView })
    }

    $baseSuffix = [EngineText]::Strip($Token)
    if ($baseSuffix.Length -gt 0) {
        $candidates.Add([pscustomobject]@{ hive = 'HKCU'; path = "Software\$baseSuffix"; view = $null })
        $candidates.Add([pscustomobject]@{ hive = 'HKLM'; path = "Software\$baseSuffix"; view = $null })
        $candidates.Add([pscustomobject]@{ hive = 'HKLM'; path = "Software\Wow6432Node\$baseSuffix"; view = '32' })
    }

    $seen = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    $ordered = [System.Collections.Generic.List[object]]::new()
    foreach ($candidate in $candidates) {
        if ($seen.Add("$($candidate.hive)`n$($candidate.path)`n$([Engine]::Repr($candidate.view))")) {
            $ordered.Add($candidate)
        }
    }

    return , $ordered.ToArray()
}

function Get-DbRegistryValueText {
    # The text branch of _match_value: str, bytes decoded as UTF-8 with replacement, int and float str(), lists joined by ", ".
    [CmdletBinding()]
    param($Value)

    if ($null -eq $Value) {
        return $null
    }

    if ($Value -is [string]) {
        return $Value
    }

    if ($Value -is [byte[]]) {
        return [EngineUtf8Decoder]::DecodeReplace($Value)
    }

    if ([Engine]::IsInt($Value) -or [Engine]::IsFloat($Value) -or $Value -is [bool]) {
        return [Engine]::Str($Value)
    }

    if ([Engine]::IsList($Value)) {
        return (@([Engine]::Iterate($Value) | ForEach-Object { [Engine]::Str($_) }) -join ', ')
    }

    return $null
}

function Search-DbRegistry {
    # search_registry(roots, spec)
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][AllowEmptyCollection()] $Roots,
        [Parameter(Mandatory = $true)] $Spec
    )

    $keywords = @($Spec.keywords | ForEach-Object { [EngineText]::Lower([string]$_) })
    $patterns = @($Spec.patterns)
    # max(0, int(max_depth)) and max(1, int(max_hits)); the counts they are compared with never leave the long range.
    $longMax = [System.Numerics.BigInteger]::new([long]::MaxValue)
    $maxDepth = [long][System.Numerics.BigInteger]::Min($longMax, [System.Numerics.BigInteger]::Max([System.Numerics.BigInteger]::Zero, [Engine]::Int($Spec.max_depth)))
    $maxHits = [long][System.Numerics.BigInteger]::Min($longMax, [System.Numerics.BigInteger]::Max([System.Numerics.BigInteger]::One, [Engine]::Int($Spec.max_hits)))
    $budget = [Math]::Max(0.1, [Engine]::Float($Spec.time_budget_s))
    $clock = [System.Diagnostics.Stopwatch]::StartNew()

    $hits = [System.Collections.Generic.List[object]]::new()
    $queue = [System.Collections.Generic.Queue[object]]::new()
    foreach ($root in @($Roots)) {
        $queue.Enqueue([pscustomobject]@{ hive = $root.hive; path = $root.path; view = $root.view; depth = [long]0 })
    }

    $seen = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    while ($queue.Count -gt 0 -and [long]$hits.Count -lt $maxHits -and $clock.Elapsed.TotalSeconds -lt $budget) {
        $key = $queue.Dequeue()
        if (-not $seen.Add("$($key.hive)`n$($key.path)`n$([Engine]::Repr($key.view))")) {
            continue
        }

        foreach ($pair in (Get-DbRegistryValue -Hive $key.hive -Path $key.path -View $key.view)) {
            $text = Get-DbRegistryValueText $pair.Data
            if ($null -eq $text) {
                continue
            }

            $name = [string]$pair.Name
            $combined = '{0} {1}' -f [EngineText]::Lower($name), [EngineText]::Lower($text)
            $missing = $false
            foreach ($keyword in $keywords) {
                if (-not $combined.Contains($keyword)) {
                    $missing = $true
                    break
                }
            }

            if ($missing) {
                continue
            }

            if ($patterns.Count -gt 0) {
                $found = $false
                foreach ($pattern in $patterns) {
                    if ($pattern.IsMatch($text)) {
                        $found = $true
                        break
                    }
                }

                if (-not $found) {
                    foreach ($pattern in $patterns) {
                        if ($pattern.IsMatch($name)) {
                            $found = $true
                            break
                        }
                    }
                }

                if (-not $found) {
                    continue
                }
            }

            $hits.Add([pscustomobject]@{
                    path         = $key.path
                    hive         = $key.hive
                    value_name   = $name
                    data_preview = [EngineText]::CodePointPrefix($text, 120)
                    reason       = 'keyword/pattern match'
                })
            if ([long]$hits.Count -ge $maxHits) {
                break
            }
        }

        if ([long]$hits.Count -ge $maxHits) {
            break
        }

        if ($key.depth -ge $maxDepth) {
            continue
        }

        foreach ($child in (Get-DbRegistrySubkey -Hive $key.hive -Path $key.path -View $key.view)) {
            $queue.Enqueue([pscustomobject]@{ hive = $key.hive; path = "$($key.path)\$child"; view = $key.view; depth = [long]($key.depth + 1) })
        }
    }

    return , $hits.ToArray()
}

function ConvertTo-DbRegistryPattern {
    # re.compile(pattern) for a registry scan pattern.
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string] $Pattern)

    try {
        return [regex]::new($Pattern, [System.Text.RegularExpressions.RegexOptions]::CultureInvariant)
    }
    catch [System.ArgumentException] {
        throw (Get-DbEngineError 'RegexParseException' $_.Exception.Message)
    }
}

function Invoke-DbRegistryScanSource {
    # The registry_scan branch of a config run: the manifest summary, and the collected file when one was written.
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] $Source,
        [Parameter(Mandatory = $true)][string] $Alias,
        [Parameter(Mandatory = $true)][string] $DestinationRoot,
        [Parameter(Mandatory = $true)] $Log
    )

    $summary = Get-DbOrderedMap
    $summary['type'] = 'registry_scan'
    $summary['token'] = $Source.token
    $summary['keywords'] = [string[]]@($Source.keywords)
    $summary['patterns'] = [string[]]@($Source.patterns)
    if (-not (Test-DbWindowsPlatform)) {
        $Log.Write('registry scan skipped: non-Windows platform')
        $summary['skipped'] = $true
        $summary['reason'] = 'not-windows'
        return [pscustomobject]@{ Summary = $summary; File = $null }
    }

    $Log.Write("registry scan started for token: $($Source.token)")
    if (@($Source.roots).Count -gt 0) {
        $roots = @($Source.roots)
    }
    else {
        $apps = Get-DbInstalledApp
        $roots = Get-DbAppRegistryRoot -Token $Source.token -Installed $apps
    }

    $spec = [pscustomobject]@{
        keywords      = @($Source.keywords)
        patterns      = @($Source.patterns | ForEach-Object { ConvertTo-DbRegistryPattern $_ })
        max_depth     = $Source.max_depth
        max_hits      = $Source.max_hits
        time_budget_s = $Source.time_budget_s
    }
    $hits = Search-DbRegistry -Roots $roots -Spec $spec

    $rootPayload = { param($root) $entry = Get-DbOrderedMap; $entry['hive'] = $root.hive; $entry['path'] = $root.path; $entry['view'] = $root.view; , $entry }
    $payload = Get-DbOrderedMap
    $payload['token'] = $Source.token
    $payload['keywords'] = [string[]]@($Source.keywords)
    $payload['patterns'] = [string[]]@($Source.patterns)
    $payload['roots'] = [object[]]@($roots | ForEach-Object { & $rootPayload $_ })
    $hitList = [System.Collections.Generic.List[object]]::new()
    foreach ($hit in $hits) {
        $entry = Get-DbOrderedMap
        $entry['hive'] = $hit.hive
        $entry['path'] = $hit.path
        $entry['value_name'] = $hit.value_name
        $entry['data_preview'] = $hit.data_preview
        $entry['reason'] = $hit.reason
        $hitList.Add($entry)
    }

    $payload['hits'] = $hitList
    if (@($Source.roots).Count -gt 0) {
        $payload['requested_roots'] = [object[]]@($Source.roots | ForEach-Object { & $rootPayload $_ })
    }

    $resultPath = [EnginePath]::Join($DestinationRoot, 'registry_scan.json')
    [EngineFile]::WriteText($resultPath, [EngineJson]::Dumps($payload, 2, $false))

    $summary['roots'] = [string[]]@($roots | ForEach-Object { '{0} \ {1}' -f $_.hive, $_.path })
    $summary['hits'] = $hits.Count
    $summary['output'] = [EnginePath]::AsPosix($resultPath)
    if (@($Source.roots).Count -gt 0) {
        $summary['requested_roots'] = [string[]]@($Source.roots | ForEach-Object {
                if ($null -eq $_.view) { '{0} \ {1}' -f $_.hive, $_.path } else { '{0} \ {1} (view {2})' -f $_.hive, $_.path, $_.view }
            })
    }

    $file = [pscustomobject]@{
        alias         = $Alias
        source        = "registry:$($Source.token)"
        destination   = $resultPath
        relative_path = 'registry_scan.json'
        size          = [System.IO.FileInfo]::new([EngineOs]::Abs($resultPath)).Length
        sha256        = [EngineFile]::HashFile($resultPath)
    }

    return [pscustomobject]@{ Summary = $summary; File = $file }
}

# DPAPI/AES package encryption: _dpapi_unprotect, _decode_key_entry, _load_encryption_keyset, _encrypt_package_file and
# _apply_package_encryption, over System.Security.Cryptography.

function Unprotect-DbDpapiBlob {
    # _dpapi_unprotect(blob, scope=scope)
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][byte[]] $Blob,
        [Parameter(Mandatory = $true)][AllowEmptyString()][string] $Scope
    )

    if (-not [EngineOs]::Windows) {
        throw (Get-DbEngineError 'PlatformNotSupportedException' 'DPAPI key decryption is only supported on Windows.')
    }

    Add-Type -AssemblyName System.Security
    $protectionScope = [System.Security.Cryptography.DataProtectionScope]::CurrentUser
    if (@('machine', 'local_machine', 'machinekey', 'local-machine') -ccontains [EngineText]::Lower($Scope)) {
        $protectionScope = [System.Security.Cryptography.DataProtectionScope]::LocalMachine
    }

    try {
        return , [System.Security.Cryptography.ProtectedData]::Unprotect($Blob, $null, $protectionScope)
    }
    catch [System.Security.Cryptography.CryptographicException] {
        throw (Get-DbEngineError 'CryptographicException' 'CryptUnprotectData failed to decrypt the key material.')
    }
}

function ConvertFrom-DbBase64Text {
    # base64.b64decode(text): binascii.a2b_base64 without strict mode (characters outside the alphabet skipped, decoding stops once
    # padding completes a quad).
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][AllowEmptyString()][string] $Text)

    foreach ($ch in $Text.ToCharArray()) {
        if ([int]$ch -gt 127) {
            throw (Get-DbEngineError 'FormatException' 'The input is not a valid Base-64 string: it holds a non-ASCII character.')
        }
    }

    $alphabet = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/'
    $bytes = [System.Collections.Generic.List[byte]]::new()
    $quadPos = 0
    $pads = 0
    $leftChar = 0
    foreach ($ch in $Text.ToCharArray()) {
        if ($ch -eq '=') {
            if ($quadPos -ge 2) {
                $pads++
                if ($quadPos + $pads -ge 4) {
                    return , $bytes.ToArray()
                }
            }

            continue
        }

        $value = $alphabet.IndexOf($ch)
        if ($value -lt 0) {
            continue
        }

        $pads = 0
        switch ($quadPos) {
            0 { $quadPos = 1; $leftChar = $value }
            1 { $quadPos = 2; $bytes.Add([byte](($leftChar -shl 2) -bor ($value -shr 4))); $leftChar = $value -band 0x0f }
            2 { $quadPos = 3; $bytes.Add([byte]((($leftChar -shl 4) -bor ($value -shr 2)) -band 0xff)); $leftChar = $value -band 0x03 }
            3 { $quadPos = 0; $bytes.Add([byte]((($leftChar -shl 6) -bor $value) -band 0xff)); $leftChar = 0 }
        }
    }

    if ($quadPos -eq 1) {
        $count = [long]([math]::Floor($bytes.Count / 3)) * 4 + 1
        throw (Get-DbEngineError 'FormatException' "The input is not a valid Base-64 string: its $count data characters are one more than a multiple of 4.")
    }

    if ($quadPos -ne 0) {
        throw (Get-DbEngineError 'FormatException' 'The input is not a valid Base-64 string: its padding is incorrect.')
    }

    return , $bytes.ToArray()
}

function ConvertFrom-DbHexText {
    # bytes.fromhex(text)
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][AllowEmptyString()][string] $Text)

    $bytes = [System.Collections.Generic.List[byte]]::new()
    $index = 0
    while ($index -lt $Text.Length) {
        $ch = $Text[$index]
        if (' ', "`t", "`n", "`r", "`f", "`v" -ccontains [string]$ch) {
            $index++
            continue
        }

        if ($index + 1 -ge $Text.Length -or -not [Uri]::IsHexDigit($ch) -or -not [Uri]::IsHexDigit($Text[$index + 1])) {
            $position = $(if ([Uri]::IsHexDigit($ch)) { $index + 1 } else { $index })
            throw (Get-DbEngineError 'FormatException' "The input is not a valid hexadecimal string: position $position is not a hexadecimal digit pair.")
        }

        $bytes.Add([System.Convert]::ToByte($Text.Substring($index, 2), 16))
        $index += 2
    }

    return , $bytes.ToArray()
}

function ConvertFrom-DbKeyEntry {
    # _decode_key_entry(entry, description=description)
    [CmdletBinding()]
    param(
        $Entry,
        [Parameter(Mandatory = $true)][string] $Description
    )

    if (-not [Engine]::IsMapping($Entry)) {
        throw (Get-DbEngineError 'InvalidDataException' "$Description must be a mapping.")
    }

    $data = [Engine]::Or([Engine]::Or([Engine]::Get($Entry, 'data', $null), [Engine]::Get($Entry, 'value', $null)), [Engine]::Get($Entry, 'key', $null))
    if ($data -isnot [string] -or [EngineText]::Strip($data).Length -eq 0) {
        throw (Get-DbEngineError 'InvalidDataException' "$Description is missing key material.")
    }

    $encoding = [EngineText]::Lower([EngineText]::Strip([Engine]::Str([Engine]::Get($Entry, 'encoding', 'base64'))))
    try {
        if ($encoding -ceq 'base64' -or $encoding -ceq 'b64') {
            $keyBytes = ConvertFrom-DbBase64Text $data
        }
        elseif ($encoding -ceq 'hex' -or $encoding -ceq 'hexadecimal') {
            $keyBytes = ConvertFrom-DbHexText ([EngineText]::Strip($data))
        }
        elseif ($encoding -ceq 'dpapi') {
            $blob = ConvertFrom-DbBase64Text $data
            $scope = [Engine]::Str([Engine]::Or([Engine]::Get($Entry, 'scope', 'current_user'), 'current_user'))
            $keyBytes = Unprotect-DbDpapiBlob -Blob $blob -Scope $scope
        }
        else {
            throw (Get-DbEngineError 'InvalidDataException' "Unsupported encoding '$encoding' for $Description.")
        }
    }
    catch {
        $engineError = Get-DbEngineException $_
        if ($null -ne $engineError -and ($engineError.ErrorType -ceq 'FormatException' -or $engineError.ErrorType -ceq 'InvalidDataException')) {
            throw (Get-DbEngineError 'InvalidDataException' "Failed to decode ${Description}: $($engineError.Message)")
        }

        throw
    }

    $minimumLength = [Engine]::Or([Engine]::Or([Engine]::Get($Entry, 'min_length', $null), [Engine]::Get($Entry, 'minimum_length', $null)), [Engine]::Get($Entry, 'length', $null))
    if ($null -ne $minimumLength) {
        $minimum = [Engine]::Int($minimumLength)
        if ([System.Numerics.BigInteger]::new($keyBytes.Length) -lt $minimum) {
            throw (Get-DbEngineError 'InvalidDataException' "$Description must be at least $minimum bytes.")
        }
    }

    return , [byte[]]$keyBytes
}

function Import-DbEncryptionKeyset {
    # _load_encryption_keyset(path): the AES key and the HMAC key.
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string] $Path)

    $payload = [EngineJson]::LoadsFile($Path)
    if (-not [Engine]::IsMapping($payload)) {
        throw (Get-DbEngineError 'InvalidDataException' 'Encryption keyset must be a JSON object.')
    }

    $schema = [Engine]::Get($payload, 'schema', $null)
    if ([Engine]::Truthy($schema) -and -not ($schema -is [string] -and $schema -ceq 'https://driftbuster.dev/offline-runner/encryption/keyset/v1')) {
        throw (Get-DbEngineError 'InvalidDataException' 'Unsupported encryption keyset schema.')
    }

    $aesEntry = [Engine]::Or([Engine]::Get($payload, 'aes_key', $null), [Engine]::Get($payload, 'aes', $null))
    $hmacEntry = [Engine]::Or([Engine]::Or([Engine]::Get($payload, 'hmac_key', $null), [Engine]::Get($payload, 'hmac', $null)), [Engine]::Get($payload, 'mac_key', $null))
    if (-not [Engine]::IsMapping($aesEntry) -or -not [Engine]::IsMapping($hmacEntry)) {
        throw (Get-DbEngineError 'InvalidDataException' "Encryption keyset must include 'aes_key' and 'hmac_key' mappings.")
    }

    $aesKey = ConvertFrom-DbKeyEntry -Entry $aesEntry -Description 'aes_key'
    $hmacKey = ConvertFrom-DbKeyEntry -Entry $hmacEntry -Description 'hmac_key'
    if (@(16, 24, 32) -notcontains $aesKey.Length) {
        throw (Get-DbEngineError 'InvalidDataException' 'AES key must be 16, 24, or 32 bytes.')
    }

    if ($aesKey.Length -ne 32) {
        throw (Get-DbEngineError 'InvalidDataException' 'AES-256 encryption requires a 32-byte AES key.')
    }

    if ($hmacKey.Length -lt 32) {
        throw (Get-DbEngineError 'InvalidDataException' 'HMAC key must be at least 32 bytes.')
    }

    return [pscustomobject]@{ AesKey = $aesKey; HmacKey = $hmacKey }
}

function Protect-DbPackageFile {
    # _encrypt_package_file(source, destination, aes_key=..., hmac_key=...): the payload written to the destination.
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string] $Source,
        [Parameter(Mandatory = $true)][string] $Destination,
        [Parameter(Mandatory = $true)][byte[]] $AesKey,
        [Parameter(Mandatory = $true)][byte[]] $HmacKey
    )

    $plaintext = [System.IO.File]::ReadAllBytes([EngineOs]::Abs($Source))
    $iv = [byte[]]::new(16)
    $random = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try {
        $random.GetBytes($iv)
    }
    finally {
        $random.Dispose()
    }

    $aes = [System.Security.Cryptography.Aes]::Create()
    try {
        $aes.Mode = [System.Security.Cryptography.CipherMode]::CBC
        $aes.Padding = [System.Security.Cryptography.PaddingMode]::PKCS7
        $aes.Key = $AesKey
        $aes.IV = $iv
        $encryptor = $aes.CreateEncryptor()
        try {
            $ciphertext = $encryptor.TransformFinalBlock($plaintext, 0, $plaintext.Length)
        }
        finally {
            $encryptor.Dispose()
        }
    }
    finally {
        $aes.Dispose()
    }

    $signed = [byte[]]::new($iv.Length + $ciphertext.Length)
    [System.Buffer]::BlockCopy($iv, 0, $signed, 0, $iv.Length)
    [System.Buffer]::BlockCopy($ciphertext, 0, $signed, $iv.Length, $ciphertext.Length)
    $hmac = [System.Security.Cryptography.HMACSHA256]::new($HmacKey)
    try {
        $mac = $hmac.ComputeHash($signed)
    }
    finally {
        $hmac.Dispose()
    }

    $package = Get-DbOrderedMap
    $package['original_name'] = [EnginePath]::Name($Source)
    $package['size'] = [long]$plaintext.Length
    $payload = Get-DbOrderedMap
    $payload['schema'] = 'https://driftbuster.dev/offline-runner/encryption/dpapi-aes/v1'
    $payload['algorithm'] = 'aes-256-cbc+hmac-sha256'
    $payload['iv'] = [System.Convert]::ToBase64String($iv)
    $payload['ciphertext'] = [System.Convert]::ToBase64String($ciphertext)
    $payload['mac'] = [System.Convert]::ToBase64String($mac)
    $payload['package'] = $package

    [EngineFile]::WriteText($Destination, [EngineJson]::Dumps($payload, 2, $false))
    return , $payload
}

function Invoke-DbPackageEncryption {
    # _apply_package_encryption(package_path, settings, base_dir=..., log=...)
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string] $PackagePath,
        [Parameter(Mandatory = $true)] $Settings,
        [AllowNull()][string] $BaseDir,
        [Parameter(Mandatory = $true)] $Log
    )

    if ($null -eq $Settings.keyset_path) {
        throw (Get-DbEngineError 'InvalidDataException' 'Encryption is enabled but keyset_path is missing.')
    }

    $resolved = $Settings.keyset_path
    if (-not [EnginePath]::IsAbsolute($resolved) -and $BaseDir) {
        $resolved = [EnginePath]::PathExpandUser([EnginePath]::Join($BaseDir, $resolved))
    }

    $resolved = [EnginePath]::PathExpandUser($resolved)
    if (-not [EngineFs]::Exists($resolved)) {
        throw (Get-DbEngineError 'FileNotFoundException' "Encryption keyset not found: $resolved")
    }

    $keys = Import-DbEncryptionKeyset -Path $resolved
    $Log.Write("loaded encryption keyset from $resolved")

    $encryptedPath = [EnginePath]::WithSuffix($PackagePath, [EnginePath]::Suffix($PackagePath) + $Settings.output_extension)
    $payload = Protect-DbPackageFile -Source $PackagePath -Destination $encryptedPath -AesKey $keys.AesKey -HmacKey $keys.HmacKey
    $Log.Write("encrypted package -> $([EnginePath]::Name($encryptedPath))")

    $removed = $false
    if ($Settings.remove_plaintext) {
        $target = [EngineOs]::Abs($PackagePath)
        if ([System.IO.File]::Exists($target)) {
            [System.IO.File]::Delete($target)
            $removed = $true
            $Log.Write('removed plaintext package after encryption')
        }
    }

    return [pscustomobject]@{ EncryptedPath = $encryptedPath; PackagePath = $PackagePath; Payload = $payload; RemovedPlaintext = $removed }
}

# Invoke-DbOfflineRunner and Invoke-DbOfflineRunnerPath: collect every source into the staging directory, write the log, manifest
# and config copy, package and optionally encrypt, and clean up.

function Get-DbHostUser {
    # The user the run is recorded under: LOGNAME, USER, LNAME or USERNAME, else the account name.
    [CmdletBinding()]
    param()

    foreach ($name in @('LOGNAME', 'USER', 'LNAME', 'USERNAME')) {
        $value = [EngineOs]::Environ($name)
        if ($value) {
            return $value
        }
    }

    return [System.Environment]::UserName
}

function Get-DbHostPlatform {
    # The platform string. On Windows: "Windows-<release>-<version>-<service pack>" from the operating system's WMI record and the
    # release table below. Elsewhere the runtime's operating system description.
    [CmdletBinding()]
    param()

    if (-not [EngineOs]::Windows) {
        return [System.Environment]::OSVersion.VersionString
    }

    try {
        $os = Get-CimInstance -ClassName Win32_OperatingSystem -ErrorAction Stop
        $version = [string]$os.Version
        $parts = @($version.Split('.') | ForEach-Object { [int]$_ })
        $clientReleases = @(
            @(10, 1, 0, 'post11'), @(10, 0, 22000, '11'), @(6, 4, 0, '10'), @(6, 3, 0, '8.1'), @(6, 2, 0, '8'), @(6, 1, 0, '7'),
            @(6, 0, 0, 'Vista'), @(5, 2, 3790, 'XP64'), @(5, 2, 0, 'XPMedia'), @(5, 1, 0, 'XP'), @(5, 0, 0, '2000'))
        $serverReleases = @(
            @(10, 1, 0, 'post2025Server'), @(10, 0, 26100, '2025Server'), @(10, 0, 20348, '2022Server'), @(10, 0, 17763, '2019Server'),
            @(6, 4, 0, '2016Server'), @(6, 3, 0, '2012ServerR2'), @(6, 2, 0, '2012Server'), @(6, 1, 0, '2008ServerR2'),
            @(6, 0, 0, '2008Server'), @(5, 2, 0, '2003Server'), @(5, 0, 0, '2000Server'))
        $releases = $(if ([int]$os.ProductType -eq 1) { $clientReleases } else { $serverReleases })
        $release = ''
        foreach ($candidate in $releases) {
            $newer = $false
            for ($index = 0; $index -lt 3; $index++) {
                $actual = $(if ($index -lt $parts.Count) { $parts[$index] } else { -1 })
                if ($candidate[$index] -ne $actual) {
                    $newer = $candidate[$index] -gt $actual
                    break
                }
            }

            if (-not $newer) {
                $release = $candidate[3]
                break
            }
        }

        $servicePack = "SP$($os.ServicePackMajorVersion)"
        if ($os.ServicePackMinorVersion -and [string]$os.ServicePackMinorVersion -ne '0') {
            $servicePack = "SP$($os.ServicePackMajorVersion).$($os.ServicePackMinorVersion)"
        }

        return ((@('Windows', $release, $version, $servicePack) | Where-Object { $_ }) -join '-').Replace(' ', '_')
    }
    catch {
        return [System.Environment]::OSVersion.VersionString
    }
}

function New-DbDirectory {
    # Path.mkdir(parents=True, exist_ok=True)
    [CmdletBinding(SupportsShouldProcess = $true)]
    param([Parameter(Mandatory = $true)][string] $Path)

    if ($PSCmdlet.ShouldProcess($Path, 'Create directory')) {
        [void][System.IO.Directory]::CreateDirectory([EngineOs]::Abs($Path))
    }
}

function Write-DbZipPackage {
    # zipfile.ZipFile(package_path, "w", ZIP_DEFLATED) holding every file under the staging directory in path order.
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string] $PackagePath,
        [Parameter(Mandatory = $true)][string] $StagingDir
    )

    Add-Type -AssemblyName System.IO.Compression
    $entries = [System.Collections.Generic.List[string]]::new()
    foreach ($file in [System.IO.Directory]::EnumerateFiles([EngineOs]::Abs($StagingDir), '*', [System.IO.SearchOption]::AllDirectories)) {
        $relative = [EnginePath]::RelativeTo($file, [EngineOs]::Abs($StagingDir))
        if ($null -ne $relative) {
            $entries.Add($relative)
        }
    }

    [EnginePath]::SortByParts($entries)
    $stream = [System.IO.FileStream]::new([EngineOs]::Abs($PackagePath), [System.IO.FileMode]::Create, [System.IO.FileAccess]::ReadWrite)
    try {
        $archive = [System.IO.Compression.ZipArchive]::new($stream, [System.IO.Compression.ZipArchiveMode]::Create, $true)
        try {
            foreach ($relative in $entries) {
                $source = [EnginePath]::Join($StagingDir, $relative)
                # ZipInfo.from_file: time.localtime(st_mtime), which Windows' C runtime refuses before the epoch; a zip header holds
                # the years 1980 to 2107.
                $modified = [System.IO.File]::GetLastWriteTime([EngineOs]::Abs($source))
                if ([EngineOs]::Windows -and $modified.ToUniversalTime() -lt [datetime]::new(1970, 1, 1, 0, 0, 0, [System.DateTimeKind]::Utc)) {
                    throw (Get-DbEngineError 'ArgumentOutOfRangeException' 'The file timestamp is before 1970 and cannot be stored.')
                }

                if ($modified.Year -lt 1980) {
                    throw (Get-DbEngineError 'ArgumentOutOfRangeException' 'ZIP does not support timestamps before 1980.')
                }

                if ($modified.Year -gt 2107) {
                    throw (Get-DbEngineError 'ArgumentOutOfRangeException' 'ZIP does not support timestamps after 2107.')
                }

                $entry = $archive.CreateEntry([EnginePath]::AsPosix($relative), [System.IO.Compression.CompressionLevel]::Optimal)
                $entry.LastWriteTime = $modified
                $output = $entry.Open()
                try {
                    $reader = [System.IO.File]::OpenRead([EngineOs]::Abs($source))
                    try {
                        $reader.CopyTo($output)
                    }
                    finally {
                        $reader.Dispose()
                    }
                }
                finally {
                    $output.Dispose()
                }
            }
        }
        finally {
            $archive.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

function Invoke-DbOfflineRunner {
    # Runs one already-loaded config.
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] $Config,
        [AllowNull()][string] $ConfigPath,
        [AllowNull()][string] $BaseDir,
        [AllowNull()][string] $Timestamp
    )

    Import-DbOfflineRunnerNative
    $runTimestamp = $(if ($Timestamp) { $Timestamp } else { Get-DbTimestamp })
    $settings = $Config.settings

    if ($ConfigPath) {
        $ConfigPath = [EnginePath]::Normalise($ConfigPath)
    }

    $effectiveBaseDir = $null
    if ($BaseDir) {
        $effectiveBaseDir = [EnginePath]::Normalise($BaseDir)
    }
    elseif ($ConfigPath) {
        $effectiveBaseDir = [EnginePath]::Parent($ConfigPath)
    }

    if ($null -ne $settings.output_directory) {
        $outputRoot = $settings.output_directory
        if ($null -ne $effectiveBaseDir -and -not [EnginePath]::IsAbsolute($outputRoot)) {
            $outputRoot = [EnginePath]::Join($effectiveBaseDir, $outputRoot)
        }
    }
    elseif ($null -ne $effectiveBaseDir) {
        $outputRoot = $effectiveBaseDir
    }
    else {
        $outputRoot = [EngineOs]::Cwd
    }

    $outputRoot = [EnginePath]::Normalise($outputRoot)
    New-DbDirectory $outputRoot

    $safeName = [EngineText]::SafeName($Config.profile.name)
    $stagingDir = [EnginePath]::Join($outputRoot, "$safeName-$runTimestamp")
    $dataRoot = [EnginePath]::Join($stagingDir, $settings.data_directory_name)
    $logsRoot = [EnginePath]::Join($stagingDir, $settings.logs_directory_name)
    New-DbDirectory $dataRoot
    New-DbDirectory $logsRoot

    $log = [RunLog]::new()
    $log.Write('offline collection started')

    $packageFilename = $null
    if ($settings.compress) {
        $packageFilename = $(if ($settings.package_name) { $settings.package_name } else { "$safeName-$runTimestamp.zip" })
        if (-not $packageFilename.ToLowerInvariant().EndsWith('.zip', [System.StringComparison]::Ordinal)) {
            $packageFilename = "$packageFilename.zip"
        }
    }

    $secretContext = Get-DbSecretContext -Options $Config.profile.options -SecretScanner $Config.profile.secret_scanner
    if (-not $secretContext.RulesLoaded) {
        $log.Write('secret detection rules unavailable; copying files without scrubbing')
    }

    $files = [System.Collections.Generic.List[object]]::new()
    $totalBytes = [long]0
    $sourceSummaries = [System.Collections.Generic.List[object]]::new()
    $sqlMetadata = [System.Collections.Generic.List[object]]::new()
    $maxTotalBytes = $settings.max_total_bytes

    $index = 0
    foreach ($source in $Config.profile.sources) {
        $alias = Get-DbDestinationName -Source $source -FallbackIndex $index
        $index++
        $destinationRoot = [EnginePath]::Join($dataRoot, $alias)
        New-DbDirectory $destinationRoot

        switch ($source.kind) {
            'sql_snapshot' {
                $outcome = Invoke-DbSqlSnapshotSource -Source $source -Alias $alias -DestinationRoot $destinationRoot -BaseDir $effectiveBaseDir `
                    -MaxTotalBytes $maxTotalBytes -TotalBytes $totalBytes -Log $log
                $sourceSummaries.Add($outcome.Summary)
                if ($null -ne $outcome.File) {
                    $files.Add($outcome.File)
                    $totalBytes += $outcome.File.size
                    $sqlMetadata.Add($outcome.Metadata)
                }
            }
            'registry_scan' {
                $outcome = Invoke-DbRegistryScanSource -Source $source -Alias $alias -DestinationRoot $destinationRoot -Log $log
                $sourceSummaries.Add($outcome.Summary)
                if ($null -ne $outcome.File) {
                    $files.Add($outcome.File)
                }
            }
            default {
                $outcome = Invoke-DbFileSource -Source $source -Alias $alias -DestinationRoot $destinationRoot -DataRoot $dataRoot `
                    -BaseDir $effectiveBaseDir -MaxTotalBytes $maxTotalBytes -TotalBytes $totalBytes -SecretContext $secretContext -Log $log
                $sourceSummaries.Add($outcome.Summary)
                foreach ($file in $outcome.Files) {
                    $files.Add($file)
                }

                $totalBytes = $outcome.TotalBytes
            }
        }
    }

    $log.Write('offline collection finished')

    $logPath = $null
    if ($settings.include_logs) {
        New-DbDirectory $logsRoot
        $logPath = [EnginePath]::Join($logsRoot, $settings.log_name)
        $log.Save($logPath)
    }

    $manifestPath = $null
    $manifest = $null
    $encryption = $settings.encryption
    if ($settings.include_manifest) {
        $hostInfo = Get-DbOrderedMap
        $hostInfo['computer_name'] = [System.Net.Dns]::GetHostName()
        $hostInfo['user'] = Get-DbHostUser
        $hostInfo['platform'] = Get-DbHostPlatform

        $profileInfo = Get-DbOrderedMap
        $profileInfo['name'] = $Config.profile.name
        $profileInfo['description'] = $Config.profile.description
        $profileInfo['baseline'] = $Config.profile.baseline
        $profileInfo['tags'] = [string[]]@($Config.profile.tags)
        $profileInfo['options'] = $Config.profile.options
        $profileInfo['secret_scanner'] = Get-DbManifestSecretScanner -Options $Config.profile.options -SecretScanner $Config.profile.secret_scanner -Context $secretContext

        $runnerInfo = Get-DbOrderedMap
        $runnerInfo['version'] = $Config.version
        $runnerInfo['schema'] = $Config.schema

        $fileEntries = [System.Collections.Generic.List[object]]::new()
        foreach ($file in $files) {
            $entry = Get-DbOrderedMap
            $entry['alias'] = $file.alias
            $entry['source'] = $file.source
            $entry['relative_path'] = $file.relative_path
            $entry['size'] = [long]$file.size
            $entry['sha256'] = $file.sha256
            $fileEntries.Add($entry)
        }

        $findings = [System.Collections.Generic.List[object]]::new()
        foreach ($finding in $secretContext.Findings) {
            $entry = Get-DbOrderedMap
            $entry['path'] = $finding.Path
            $entry['rule'] = $finding.Rule
            $entry['line'] = $finding.Line
            $entry['snippet'] = $finding.Snippet
            $findings.Add($entry)
        }

        $ignoredRules = [System.Collections.Generic.List[string]]::new($secretContext.IgnoreRules)
        $ignoredRules.Sort([System.Comparison[string]] { param($left, $right) [EngineText]::CompareCodePoints($left, $right) })
        $secrets = Get-DbOrderedMap
        $secrets['ruleset_version'] = $secretContext.Version
        $secrets['findings'] = $findings
        $secrets['ignored_rules'] = $ignoredRules
        $secrets['ignored_patterns'] = $secretContext.IgnorePatternText

        $package = Get-DbOrderedMap
        $package['staging_directory'] = $stagingDir
        $package['data_directory'] = $dataRoot
        $package['logs_directory'] = $logsRoot
        $package['compressed'] = [bool]$settings.compress
        $package['cleanup_staging'] = [bool]$settings.cleanup_staging
        if ($packageFilename) {
            $package['package_name'] = $packageFilename
        }

        $encryptionInfo = Get-DbOrderedMap
        if ($null -ne $encryption -and $encryption.enabled) {
            $encryptedName = $(if ($packageFilename) { $packageFilename } else { "$safeName-$runTimestamp.zip" })
            if (-not $encryptedName.EndsWith($encryption.output_extension, [System.StringComparison]::Ordinal)) {
                $encryptedName = "$encryptedName$($encryption.output_extension)"
            }

            $encryptionInfo['enabled'] = $true
            $encryptionInfo['mode'] = $encryption.mode
            $encryptionInfo['output_name'] = $encryptedName
            $encryptionInfo['remove_plaintext'] = [bool]$encryption.remove_plaintext
            $encryptionInfo['keyset_path'] = $encryption.keyset_path
            $encryptionInfo['schema'] = 'https://driftbuster.dev/offline-runner/encryption/dpapi-aes/v1'
            $encryptionInfo['algorithm'] = 'aes-256-cbc+hmac-sha256'
        }
        else {
            $encryptionInfo['enabled'] = $false
        }

        $package['encryption'] = $encryptionInfo

        $metadata = Get-DbOrderedMap
        foreach ($key in @($Config.metadata.Keys)) {
            $metadata[$key] = $Config.metadata[$key]
        }

        if ($sqlMetadata.Count -gt 0) {
            $metadata['sql_exports'] = $sqlMetadata
        }

        $manifest = Get-DbOrderedMap
        $manifest['schema'] = 'https://driftbuster.dev/offline-runner/manifest/v1'
        $manifest['generated_at'] = [RunLog]::Stamp()
        $manifest['timestamp'] = $runTimestamp
        $manifest['host'] = $hostInfo
        $manifest['profile'] = $profileInfo
        $manifest['runner'] = $runnerInfo
        $manifest['sources'] = $sourceSummaries
        $manifest['files'] = $fileEntries
        $manifest['secrets'] = $secrets
        $manifest['metadata'] = $metadata
        $manifest['package'] = $package

        if ($ConfigPath -and [EngineFs]::Exists($ConfigPath)) {
            $configInfo = Get-DbOrderedMap
            $configInfo['path'] = $ConfigPath
            $configInfo['sha256'] = [EngineFile]::HashFile($ConfigPath)
            $manifest['config'] = $configInfo
        }

        $manifestPath = [EnginePath]::Join($stagingDir, $settings.manifest_name)
        [EngineFile]::WriteText($manifestPath, [EngineJson]::Dumps($manifest, 2, $true))
    }

    if ($settings.include_config -and $ConfigPath -and [EngineFs]::Exists($ConfigPath)) {
        $configCopy = [EnginePath]::Join($stagingDir, [EnginePath]::Name($ConfigPath))
        [System.IO.File]::Copy([EngineOs]::Abs($ConfigPath), [EngineOs]::Abs($configCopy), $true)
        [EngineFile]::CopyStat($ConfigPath, $configCopy)
    }

    $packagePath = $null
    $encryptedPackagePath = $null
    $unencryptedPackagePath = $null
    $encryptionPayload = $null
    $stagingOnDisk = $stagingDir
    $manifestOnDisk = $manifestPath
    $logOnDisk = $logPath
    if ($settings.compress) {
        $packagePath = [EnginePath]::Join($outputRoot, $packageFilename)
        Write-DbZipPackage -PackagePath $packagePath -StagingDir $stagingDir
        $unencryptedPackagePath = $packagePath

        if ($null -ne $encryption -and $encryption.enabled) {
            $applied = Invoke-DbPackageEncryption -PackagePath $packagePath -Settings $encryption -BaseDir $effectiveBaseDir -Log $log
            $encryptedPackagePath = $applied.EncryptedPath
            $packagePath = $applied.EncryptedPath
            $unencryptedPackagePath = $applied.PackagePath
            $encryptionPayload = $applied.Payload

            if ($null -ne $manifest) {
                $manifest['package']['encryption']['output_name'] = [EnginePath]::Name($applied.EncryptedPath)
                $manifest['package']['encryption']['sha256'] = [EngineFile]::HashFile($applied.EncryptedPath)
                $manifest['package']['encryption']['removed_plaintext'] = [bool]$applied.RemovedPlaintext
                if ($manifestPath) {
                    [EngineFile]::WriteText($manifestPath, [EngineJson]::Dumps($manifest, 2, $true))
                }
            }
        }

        if ($settings.cleanup_staging) {
            try {
                [System.IO.Directory]::Delete([EngineOs]::Abs($stagingDir), $true)
            }
            catch [System.IO.IOException], [System.UnauthorizedAccessException] {
                Write-Verbose "staging directory not removed: $($_.Exception.Message)"
            }

            $stagingOnDisk = $null
            $manifestOnDisk = $null
            $logOnDisk = $null
        }
    }
    elseif ($null -ne $encryption -and $encryption.enabled) {
        throw (Get-DbEngineError 'InvalidDataException' 'Encryption requires compression to be enabled.')
    }

    return [pscustomobject]@{
        config                   = $Config
        staging_dir              = $stagingOnDisk
        manifest_path            = $manifestOnDisk
        log_path                 = $logOnDisk
        package_path             = $packagePath
        files                    = $files.ToArray()
        timestamp                = $runTimestamp
        encrypted_package_path   = $encryptedPackagePath
        unencrypted_package_path = $unencryptedPackagePath
        encryption_payload       = $encryptionPayload
        secret_context           = $secretContext
        log                      = $log
    }
}

function Invoke-DbOfflineRunnerPath {
    # Loads a config from disk and runs it.
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string] $ConfigPath,
        [AllowNull()][string] $BaseDir,
        [AllowNull()][string] $Timestamp
    )

    Import-DbOfflineRunnerNative
    $config = Import-DbOfflineRunnerConfig -Path $ConfigPath
    return Invoke-DbOfflineRunner -Config $config -ConfigPath $ConfigPath -BaseDir $BaseDir -Timestamp $Timestamp
}

Import-DbOfflineRunnerNative

# Dot-sourcing the script (tests) loads the helpers above and stops here.
if ($MyInvocation.InvocationName -eq '.') {
    return
}

$ErrorActionPreference = 'Stop'

try {
    [DriftBusterOfflineRunner.EngineOs]::Cwd = (Get-Location -PSProvider FileSystem).ProviderPath
    $resolvedConfig = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($ConfigPath)
    $config = Import-DbOfflineRunnerConfig -Path $resolvedConfig
    if ($OutputDirectory) {
        $resolvedOutput = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
        $config.settings.output_directory = [DriftBusterOfflineRunner.EnginePath]::Normalise($resolvedOutput)
    }

    $result = Invoke-DbOfflineRunner -Config $config -ConfigPath $resolvedConfig
}
catch {
    $engineError = Get-DbEngineException $_
    if ($null -eq $engineError) {
        throw
    }

    throw [System.InvalidOperationException]::new(('{0}: {1}' -f $engineError.ErrorType, $engineError.Message), $engineError)
}

Write-Output ([pscustomobject]@{
        StagingDirectory       = $result.staging_dir
        PackagePath            = $result.package_path
        EncryptedPackagePath   = $result.encrypted_package_path
        UnencryptedPackagePath = $result.unencrypted_package_path
        ManifestPath           = $result.manifest_path
        LogPath                = $result.log_path
        FilesCollected         = @($result.files).Count
    })
