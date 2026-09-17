namespace DriftBuster.Backend.Infrastructure.EngineRe;

/// <summary>The <c>SRE_AT_*</c> codes a compiled <c>AT</c> carries, after <c>AT_MULTILINE</c> and <c>AT_UNICODE</c> mapping.</summary>
internal enum ReAtCode
{
    /// <summary><c>SRE_AT_BEGINNING</c>.</summary>
    Beginning,

    /// <summary><c>SRE_AT_BEGINNING_LINE</c>.</summary>
    BeginningLine,

    /// <summary><c>SRE_AT_BEGINNING_STRING</c>.</summary>
    BeginningString,

    /// <summary><c>SRE_AT_BOUNDARY</c> (ASCII word characters).</summary>
    Boundary,

    /// <summary><c>SRE_AT_NON_BOUNDARY</c> (ASCII word characters).</summary>
    NonBoundary,

    /// <summary><c>SRE_AT_END</c>.</summary>
    End,

    /// <summary><c>SRE_AT_END_LINE</c>.</summary>
    EndLine,

    /// <summary><c>SRE_AT_END_STRING</c>.</summary>
    EndString,

    /// <summary><c>SRE_AT_UNI_BOUNDARY</c>.</summary>
    UniBoundary,

    /// <summary><c>SRE_AT_UNI_NON_BOUNDARY</c>.</summary>
    UniNonBoundary,
}
