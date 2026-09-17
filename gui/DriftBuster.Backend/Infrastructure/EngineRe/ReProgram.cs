namespace DriftBuster.Backend.Infrastructure.EngineRe;

/// <summary>
/// A compiled pattern: the <c>_sre</c> code list, the code point sets its <see cref="ReOpcode.Char"/> operations index, the
/// minimum match width from the <c>INFO</c> block (capped at <c>MAXCODE</c> as <c>_compile_info</c> caps it), the number of
/// capture groups, and the set every match must start with (null when a match may be empty or its first code point is not
/// known). <see cref="PositionalTails"/> holds the code positions (the skip operand) of the top-level greedy single-character
/// repeats whose tail, the rest of the pattern, succeeds or fails by text position alone (see <see cref="ReCompiler"/>).
/// </summary>
internal sealed record ReProgram(long[] Code, ReCharSet[] Sets, long MinWidth, int Groups, ReCharSet? FirstSet, IReadOnlySet<int> PositionalTails);
