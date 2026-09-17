namespace DriftBuster.Backend.Infrastructure.EngineRe;

/// <summary>
/// The <c>_sre</c> opcodes <see cref="ReCompiler"/> emits. Every single-character opcode (<c>LITERAL</c>, <c>NOT_LITERAL</c>,
/// <c>ANY</c>, <c>ANY_ALL</c>, <c>IN</c> and their <c>_IGNORE</c> forms) is folded into <see cref="Char"/> over the code point
/// set the opcode accepts (<see cref="ReCharacterSets.NodeSet"/>), so no charset subprogram is interpreted.
/// </summary>
internal enum ReOpcode
{
    /// <summary><c>FAILURE</c>; also the zero that terminates a <c>BRANCH</c> alternative list.</summary>
    Failure = 0,

    /// <summary><c>SUCCESS</c>.</summary>
    Success,

    /// <summary><c>AT &lt;code&gt;</c>, the code already mapped for MULTILINE and UNICODE.</summary>
    At,

    /// <summary>A single-character test: <c>CHAR &lt;set index&gt;</c>.</summary>
    Char,

    /// <summary><c>BRANCH &lt;skip&gt; alternative JUMP ... 0</c>.</summary>
    Branch,

    /// <summary><c>JUMP &lt;offset&gt;</c>.</summary>
    Jump,

    /// <summary><c>MARK &lt;gid&gt;</c>.</summary>
    Mark,

    /// <summary><c>REPEAT &lt;skip&gt; &lt;min&gt; &lt;max&gt; item MAX_UNTIL|MIN_UNTIL</c>.</summary>
    Repeat,

    /// <summary><c>MAX_UNTIL</c>.</summary>
    MaxUntil,

    /// <summary><c>MIN_UNTIL</c>.</summary>
    MinUntil,

    /// <summary><c>REPEAT_ONE &lt;skip&gt; &lt;min&gt; &lt;max&gt; item SUCCESS</c>.</summary>
    RepeatOne,

    /// <summary><c>MIN_REPEAT_ONE &lt;skip&gt; &lt;min&gt; &lt;max&gt; item SUCCESS</c>.</summary>
    MinRepeatOne,

    /// <summary><c>POSSESSIVE_REPEAT &lt;skip&gt; &lt;min&gt; &lt;max&gt; item SUCCESS</c>.</summary>
    PossessiveRepeat,

    /// <summary><c>POSSESSIVE_REPEAT_ONE &lt;skip&gt; &lt;min&gt; &lt;max&gt; item SUCCESS</c>.</summary>
    PossessiveRepeatOne,

    /// <summary><c>ATOMIC_GROUP &lt;skip&gt; body SUCCESS</c>.</summary>
    AtomicGroup,

    /// <summary><c>ASSERT &lt;skip&gt; &lt;back&gt; body SUCCESS</c>.</summary>
    Assert,

    /// <summary><c>ASSERT_NOT &lt;skip&gt; &lt;back&gt; body SUCCESS</c>.</summary>
    AssertNot,

    /// <summary><c>GROUPREF &lt;group&gt;</c>.</summary>
    GroupRef,

    /// <summary><c>GROUPREF_IGNORE &lt;group&gt;</c> (ASCII lowercase).</summary>
    GroupRefIgnore,

    /// <summary><c>GROUPREF_UNI_IGNORE &lt;group&gt;</c> (<c>sre_lower_unicode</c>).</summary>
    GroupRefUniIgnore,

    /// <summary><c>GROUPREF_EXISTS &lt;group&gt; &lt;skip&gt; yes JUMP no</c>.</summary>
    GroupRefExists,
}
