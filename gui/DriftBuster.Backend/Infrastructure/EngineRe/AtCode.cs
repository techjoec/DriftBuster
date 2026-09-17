namespace DriftBuster.Backend.Infrastructure.EngineRe;

/// <summary>The <c>AT</c> position codes the parser produces.</summary>
internal enum AtCode
{
    /// <summary><c>^</c>.</summary>
    Beginning,

    /// <summary><c>\A</c>.</summary>
    BeginningString,

    /// <summary><c>\b</c>.</summary>
    Boundary,

    /// <summary><c>\B</c>.</summary>
    NonBoundary,

    /// <summary><c>$</c>.</summary>
    End,

    /// <summary><c>\Z</c>.</summary>
    EndString,
}
