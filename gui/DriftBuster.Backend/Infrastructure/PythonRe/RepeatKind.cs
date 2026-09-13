namespace DriftBuster.Backend.Infrastructure.PythonRe;

/// <summary>The three repeat opcodes.</summary>
internal enum RepeatKind
{
    /// <summary><c>MAX_REPEAT</c>: greedy.</summary>
    Greedy,

    /// <summary><c>MIN_REPEAT</c>: lazy.</summary>
    Lazy,

    /// <summary><c>POSSESSIVE_REPEAT</c>.</summary>
    Possessive,
}
