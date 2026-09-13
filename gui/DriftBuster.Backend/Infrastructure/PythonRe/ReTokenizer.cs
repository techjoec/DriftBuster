using System.Text;

namespace DriftBuster.Backend.Infrastructure.PythonRe;

/// <summary>
/// <c>re._parser.Tokenizer</c> over the pattern's code points: a token is one code point, or a backslash and the code
/// point after it. Tokens are handled as strings of code points (<see cref="CodePoints"/>).
/// </summary>
internal sealed class ReTokenizer
{
    private readonly int[] _codes;
    private int _index;

    public ReTokenizer(string pattern)
    {
        Pattern = pattern;
        _codes = CodePoints(pattern);
        Advance();
    }

    public string Pattern { get; }

    /// <summary><c>source.next</c>: the pending token, or null at the end of the pattern.</summary>
    public int[]? Next { get; private set; }

    /// <summary><c>tell()</c> / <c>pos</c>: the code point offset of <see cref="Next"/>.</summary>
    public int Tell() => _index - (Next?.Length ?? 0);

    public static int[] CodePoints(string text)
    {
        var codes = new List<int>(text.Length);
        for (var offset = 0; offset < text.Length; offset++)
        {
            var ch = text[offset];
            if (char.IsHighSurrogate(ch) && offset + 1 < text.Length && char.IsLowSurrogate(text[offset + 1]))
            {
                codes.Add(char.ConvertToUtf32(ch, text[offset + 1]));
                offset++;
            }
            else
            {
                codes.Add(ch);
            }
        }

        return [.. codes];
    }

    /// <summary>The UTF-16 spelling of a code point sequence; a lone surrogate code point is written as that one unit.</summary>
    public static string Text(IEnumerable<int> codes)
    {
        var builder = new StringBuilder();
        foreach (var code in codes)
        {
            if (code > 0xFFFF)
            {
                builder.Append(char.ConvertFromUtf32(code));
            }
            else
            {
                builder.Append((char)code);
            }
        }

        return builder.ToString();
    }

    public static bool Is(int[]? token, char ch) => token is { Length: 1 } && token[0] == ch;

    public static bool IsIn(int[]? token, string chars) => token is { Length: 1 } && token[0] < 0x10000 && chars.Contains((char)token[0], StringComparison.Ordinal);

    public bool Match(char ch)
    {
        if (!Is(Next, ch))
        {
            return false;
        }

        Advance();
        return true;
    }

    public int[]? Get()
    {
        var current = Next;
        Advance();
        return current;
    }

    /// <summary><c>getwhile(n, charset)</c> for single-code-point members of <paramref name="chars"/>.</summary>
    public List<int> GetWhile(int count, string chars)
    {
        var result = new List<int>();
        for (var i = 0; i < count; i++)
        {
            if (!IsIn(Next, chars))
            {
                break;
            }

            result.Add(Next![0]);
            Advance();
        }

        return result;
    }

    public string GetUntil(char terminator, string name)
    {
        var result = new List<int>();
        while (true)
        {
            var token = Next;
            Advance();
            if (token is null)
            {
                if (result.Count == 0)
                {
                    throw Error("missing " + name);
                }

                throw Error($"missing {terminator}, unterminated name", result.Count);
            }

            if (Is(token, terminator))
            {
                if (result.Count == 0)
                {
                    throw Error("missing " + name, 1);
                }

                break;
            }

            result.AddRange(token);
        }

        return Text(result);
    }

    public void Seek(int index)
    {
        _index = index;
        Advance();
    }

    public PythonReException Error(string message, int offset = 0) => new(message, Tell() - offset);

    public void CheckGroupName(string name, int offset)
    {
        if (!PythonIdentifier.IsIdentifier(name))
        {
            throw Error($"bad character in group name {PythonRepr.StrRepr(name)}", CodePoints(name).Length + offset);
        }
    }

    private void Advance()
    {
        var index = _index;
        if (index >= _codes.Length)
        {
            Next = null;
            return;
        }

        var code = _codes[index];
        if (code == '\\')
        {
            index++;
            if (index >= _codes.Length)
            {
                throw new PythonReException("bad escape (end of pattern)", _codes.Length - 1);
            }

            Next = [code, _codes[index]];
        }
        else
        {
            Next = [code];
        }

        _index = index + 1;
    }
}
