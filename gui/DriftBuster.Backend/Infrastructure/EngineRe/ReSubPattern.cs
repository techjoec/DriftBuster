namespace DriftBuster.Backend.Infrastructure.EngineRe;

/// <summary><c>re._parser.SubPattern</c>: a node sequence with its cached <c>getwidth()</c>.</summary>
internal sealed class ReSubPattern(ReParseState state)
{
    /// <summary><c>MAXWIDTH</c> (1 &lt;&lt; 64): the saturation bound of <see cref="GetWidth"/>.</summary>
    public static readonly UInt128 MaxWidth = UInt128.One << 64;

    private (UInt128 Low, UInt128 High)? _width;

    public ReParseState State { get; } = state;

    public List<ReNode> Nodes { get; } = [];

    /// <summary><c>getwidth()</c>: the minimum and maximum code point length a match of this sequence can have.</summary>
    public (UInt128 Low, UInt128 High) GetWidth()
    {
        if (_width is { } cached)
        {
            return cached;
        }

        UInt128 low = 0;
        UInt128 high = 0;
        foreach (var node in Nodes)
        {
            if (node is ReNode.Failure)
            {
                continue;
            }

            var (nodeLow, nodeHigh) = NodeWidth(node);
            low = Saturate(low + nodeLow);
            high = Saturate(high + nodeHigh);
        }

        _width = (low, high);
        return _width.Value;
    }

    private (UInt128 Low, UInt128 High) NodeWidth(ReNode node)
    {
        switch (node)
        {
            case ReNode.Branch branch:
                var low = MaxWidth;
                UInt128 high = 0;
                foreach (var alternative in branch.Alternatives)
                {
                    var (l, h) = alternative.GetWidth();
                    low = UInt128.Min(low, l);
                    high = UInt128.Max(high, h);
                }

                return (low, high);
            case ReNode.Atomic atomic:
                return atomic.Body.GetWidth();
            case ReNode.Subpattern group:
                return group.Body.GetWidth();
            case ReNode.Repeat repeat:
                var (itemLow, itemHigh) = repeat.Item.GetWidth();
                var repeatHigh = repeat.Max == ReNode.MaxRepeat && itemHigh != 0 ? MaxWidth : Saturate(itemHigh * (ulong)repeat.Max);
                return (Saturate(itemLow * (ulong)repeat.Min), repeatHigh);
            case ReNode.Literal or ReNode.In or ReNode.Any:
                return (1, 1);
            case ReNode.GroupRef reference:
                return State.GroupWidths[reference.Group]!.Value;
            case ReNode.GroupRefExists conditional:
                var (yesLow, yesHigh) = conditional.Yes.GetWidth();
                if (conditional.No is null)
                {
                    return (0, yesHigh);
                }

                var (noLow, noHigh) = conditional.No.GetWidth();
                return (UInt128.Min(yesLow, noLow), UInt128.Max(yesHigh, noHigh));
            default:
                return (0, 0);
        }
    }

    private static UInt128 Saturate(UInt128 value) => UInt128.Min(value, MaxWidth);
}
