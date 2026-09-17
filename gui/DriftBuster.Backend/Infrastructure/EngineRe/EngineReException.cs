namespace DriftBuster.Backend.Infrastructure.EngineRe;

/// <summary><c>re.error</c> (<c>re.PatternError</c>): the pattern does not compile.</summary>
public sealed class EngineReException : Exception
{
    public EngineReException()
    {
    }

    public EngineReException(string message)
        : base(message)
    {
    }

    public EngineReException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public EngineReException(string message, int? position)
        : base(message)
    {
        Position = position;
    }

    /// <summary><c>error.pos</c>: the code point offset in the pattern, or null when the compiler raised it.</summary>
    public int? Position { get; }
}
