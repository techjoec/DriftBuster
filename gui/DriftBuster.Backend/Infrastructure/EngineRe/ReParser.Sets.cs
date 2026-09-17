using System.Globalization;
using System.Numerics;

namespace DriftBuster.Backend.Infrastructure.EngineRe;

/// <summary>Character sets and repeats.</summary>
internal static partial class ReParser
{
    private static ReNode ParseSet(ReTokenizer source)
    {
        var here = source.Tell() - 1;
        var items = new List<ReNode.SetItem>();
        var negate = source.Match('^');
        while (true)
        {
            var token = source.Get() ?? throw source.Error("unterminated character set", source.Tell() - here);
            if (ReTokenizer.Is(token, ']') && items.Count > 0)
            {
                break;
            }

            var first = token[0] == '\\' ? ClassEscape(source, token) : new ReNode.SetItem.Literal(token[0]);
            if (!source.Match('-'))
            {
                items.Add(first);
                continue;
            }

            var that = source.Get() ?? throw source.Error("unterminated character set", source.Tell() - here);
            if (ReTokenizer.Is(that, ']'))
            {
                items.Add(first);
                items.Add(new ReNode.SetItem.Literal('-'));
                break;
            }

            var second = that[0] == '\\' ? ClassEscape(source, that) : new ReNode.SetItem.Literal(that[0]);
            if (first is not ReNode.SetItem.Literal low || second is not ReNode.SetItem.Literal high || high.Code < low.Code)
            {
                throw source.Error($"bad character range {TokenText(token)}-{TokenText(that)}", token.Length + 1 + that.Length);
            }

            items.Add(new ReNode.SetItem.Range(low.Code, high.Code));
        }

        if (items.Count == 1 && items[0] is ReNode.SetItem.Literal single)
        {
            return new ReNode.Literal(single.Code, negate);
        }

        if (negate)
        {
            items.Insert(0, new ReNode.SetItem.Negate());
        }

        return new ReNode.In(items);
    }

    private static void ParseRepeat(ReTokenizer source, ReSubPattern subpattern, int[] token)
    {
        var here = source.Tell();
        long min;
        long max;
        switch ((char)token[0])
        {
            case '?':
                (min, max) = (0, 1);
                break;
            case '*':
                (min, max) = (0, ReNode.MaxRepeat);
                break;
            case '+':
                (min, max) = (1, ReNode.MaxRepeat);
                break;
            default:
                if (!TryParseBraces(source, subpattern, here, out min, out max))
                {
                    return;
                }

                break;
        }

        var last = subpattern.Nodes.Count > 0 ? subpattern.Nodes[^1] : null;
        if (last is null or ReNode.At)
        {
            throw source.Error("nothing to repeat", source.Tell() - here + token.Length);
        }

        if (last is ReNode.Repeat)
        {
            throw source.Error("multiple repeat", source.Tell() - here + token.Length);
        }

        ReSubPattern item;
        if (last is ReNode.Subpattern { Group: null, AddFlags: EngineReFlags.None, DelFlags: EngineReFlags.None } bare)
        {
            item = bare.Body;
        }
        else
        {
            item = new ReSubPattern(subpattern.State);
            item.Nodes.Add(last);
        }

        var kind = source.Match('?') ? RepeatKind.Lazy : source.Match('+') ? RepeatKind.Possessive : RepeatKind.Greedy;
        subpattern.Nodes[^1] = new ReNode.Repeat(kind, min, max, item);
    }

    // The "{" branch: false when the braces are not a repeat and "{" was added as a literal.
    private static bool TryParseBraces(ReTokenizer source, ReSubPattern subpattern, int here, out long min, out long max)
    {
        (min, max) = (0, ReNode.MaxRepeat);
        if (ReTokenizer.Is(source.Next, '}'))
        {
            subpattern.Nodes.Add(new ReNode.Literal('{', negated: false));
            return false;
        }

        var low = source.GetWhile(int.MaxValue, Digits);
        List<int> high;
        if (source.Match(','))
        {
            high = source.GetWhile(int.MaxValue, Digits);
        }
        else
        {
            high = low;
        }

        if (!source.Match('}'))
        {
            subpattern.Nodes.Add(new ReNode.Literal('{', negated: false));
            source.Seek(here);
            return false;
        }

        if (low.Count > 0)
        {
            min = RepeatCount(low);
        }

        if (high.Count > 0)
        {
            max = RepeatCount(high);
            if (max < min)
            {
                throw source.Error("min repeat greater than max repeat", source.Tell() - here);
            }
        }

        return true;
    }

    private static long RepeatCount(List<int> digits)
    {
        var value = BigInteger.Parse(ReTokenizer.Text(digits), NumberStyles.None, CultureInfo.InvariantCulture);
        if (value >= ReNode.MaxRepeat)
        {
            throw new OverflowException("the repetition number is too large");
        }

        return (long)value;
    }
}
