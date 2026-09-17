namespace DriftBuster.Backend.Infrastructure.EngineRe;

/// <summary>
/// The code points a match must start with, when every match consumes at least one code point: a conservative stand-in
/// for the literal prefix and charset of <c>_compile_info</c>, which only let <c>_sre</c>'s search skip start positions
/// where no match can begin.
/// </summary>
/// <remarks>
/// Zero-width nodes (anchors, look-arounds) constrain nothing and are passed over; a back-reference or conditional may
/// consume anything, so it makes the set unbounded. A pattern that can match the empty string gets no filter, which keeps
/// the empty-match rules of <c>finditer</c> untouched.
/// </remarks>
internal static class ReFirstSet
{
    public static ReCharSet? Of(ReSubPattern pattern, EngineReFlags flags)
    {
        var (set, nullable) = Sequence(pattern, flags);
        return nullable || set.SetEquals(CodePointSet.All) ? null : new ReCharSet(set);
    }

    private static (CodePointSet Set, bool Nullable) Sequence(ReSubPattern pattern, EngineReFlags flags)
    {
        var accumulated = CodePointSet.Empty;
        foreach (var node in pattern.Nodes)
        {
            var (set, nullable) = Node(node, flags);
            accumulated = accumulated.Union(set);
            if (!nullable)
            {
                return (accumulated, false);
            }
        }

        return (accumulated, true);
    }

    private static (CodePointSet Set, bool Nullable) Node(ReNode node, EngineReFlags flags)
    {
        switch (node)
        {
            case ReNode.Literal or ReNode.In or ReNode.Any:
                return (ReCharacterSets.NodeSet(node, flags), false);
            case ReNode.Subpattern group:
                return Sequence(group.Body, ReCompiler.CombineFlags(flags, group.AddFlags, group.DelFlags));
            case ReNode.Atomic atomic:
                return Sequence(atomic.Body, flags);
            case ReNode.Repeat repeat:
                var (itemSet, itemNullable) = Sequence(repeat.Item, flags);
                return (itemSet, itemNullable || repeat.Min == 0);
            case ReNode.Branch branch:
                var union = CodePointSet.Empty;
                var anyNullable = false;
                foreach (var alternative in branch.Alternatives)
                {
                    var (set, nullable) = Sequence(alternative, flags);
                    union = union.Union(set);
                    anyNullable |= nullable;
                }

                return (union, anyNullable);
            case ReNode.At or ReNode.Assert or ReNode.Failure:
                return (CodePointSet.Empty, true);
            default:
                return (CodePointSet.All, true);
        }
    }
}
