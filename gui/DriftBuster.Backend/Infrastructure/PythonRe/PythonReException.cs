namespace DriftBuster.Backend.Infrastructure.PythonRe;

/// <summary><c>re.error</c> (<c>re.PatternError</c>): the pattern does not compile.</summary>
public sealed class PythonReException : Exception
{
    public PythonReException()
    {
    }

    public PythonReException(string message)
        : base(message)
    {
    }

    public PythonReException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public PythonReException(string message, int? position)
        : base(message)
    {
        Position = position;
    }

    /// <summary><c>error.pos</c>: the code point offset in the pattern, or null when the compiler raised it.</summary>
    public int? Position { get; }
}
