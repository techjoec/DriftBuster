namespace DriftBuster.Backend.Infrastructure.EngineRe;

/// <summary>One <c>(op, av)</c> item of an <c>re._parser.SubPattern</c>.</summary>
internal abstract class ReNode
{
    /// <summary><c>MAXREPEAT</c>: the unbounded repeat count.</summary>
    public const long MaxRepeat = 4294967295;

    /// <summary><c>LITERAL</c> or, when <see cref="Negated"/>, <c>NOT_LITERAL</c>.</summary>
    public sealed class Literal(int code, bool negated) : ReNode
    {
        public int Code { get; } = code;

        public bool Negated { get; } = negated;
    }

    /// <summary><c>IN</c>: a character set.</summary>
    public sealed class In(IReadOnlyList<SetItem> items) : ReNode
    {
        public IReadOnlyList<SetItem> Items { get; } = items;
    }

    /// <summary><c>ANY</c>.</summary>
    public sealed class Any : ReNode;

    /// <summary><c>AT</c>: a zero-width position test.</summary>
    public sealed class At(AtCode code) : ReNode
    {
        public AtCode Code { get; } = code;
    }

    /// <summary><c>BRANCH</c>.</summary>
    public sealed class Branch(IReadOnlyList<ReSubPattern> alternatives) : ReNode
    {
        public IReadOnlyList<ReSubPattern> Alternatives { get; } = alternatives;
    }

    /// <summary><c>MAX_REPEAT</c>, <c>MIN_REPEAT</c> or <c>POSSESSIVE_REPEAT</c>.</summary>
    public sealed class Repeat(RepeatKind kind, long min, long max, ReSubPattern item) : ReNode
    {
        public RepeatKind Kind { get; } = kind;

        public long Min { get; } = min;

        public long Max { get; } = max;

        public ReSubPattern Item { get; } = item;
    }

    /// <summary><c>SUBPATTERN</c>: a capturing group (when <see cref="Group"/> is set) or a scoped-flags group.</summary>
    public sealed class Subpattern(int? group, EngineReFlags addFlags, EngineReFlags delFlags, ReSubPattern body) : ReNode
    {
        public int? Group { get; } = group;

        public EngineReFlags AddFlags { get; } = addFlags;

        public EngineReFlags DelFlags { get; } = delFlags;

        public ReSubPattern Body { get; } = body;
    }

    /// <summary><c>ATOMIC_GROUP</c>.</summary>
    public sealed class Atomic(ReSubPattern body) : ReNode
    {
        public ReSubPattern Body { get; } = body;
    }

    /// <summary><c>ASSERT</c> or, when <see cref="Negative"/>, <c>ASSERT_NOT</c>; <see cref="Behind"/> for look-behind.</summary>
    public sealed class Assert(bool negative, bool behind, ReSubPattern body) : ReNode
    {
        public bool Negative { get; } = negative;

        public bool Behind { get; } = behind;

        public ReSubPattern Body { get; } = body;
    }

    /// <summary><c>FAILURE</c> (an empty negative look-ahead).</summary>
    public sealed class Failure : ReNode;

    /// <summary><c>GROUPREF</c>.</summary>
    public sealed class GroupRef(int group) : ReNode
    {
        public int Group { get; } = group;
    }

    /// <summary><c>GROUPREF_EXISTS</c>: <c>(?(group)yes|no)</c>.</summary>
    public sealed class GroupRefExists(int group, ReSubPattern yes, ReSubPattern? no) : ReNode
    {
        public int Group { get; } = group;

        public ReSubPattern Yes { get; } = yes;

        public ReSubPattern? No { get; } = no;
    }

    /// <summary>A member of a character set.</summary>
    public abstract class SetItem
    {
        public sealed class Negate : SetItem;

        public sealed class Literal(int code) : SetItem
        {
            public int Code { get; } = code;
        }

        public sealed class Range(int low, int high) : SetItem
        {
            public int Low { get; } = low;

            public int High { get; } = high;
        }

        public sealed class Category(ReCategory code) : SetItem
        {
            public ReCategory Code { get; } = code;
        }
    }
}
