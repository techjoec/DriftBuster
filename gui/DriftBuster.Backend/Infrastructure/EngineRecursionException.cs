namespace DriftBuster.Backend.Infrastructure;

/// <summary>Python's <c>RecursionError</c>, with Python's message text.</summary>
public sealed class EngineRecursionException : InvalidOperationException
{
    public EngineRecursionException()
        : this("maximum recursion depth exceeded")
    {
    }

    public EngineRecursionException(string message)
        : base(message)
    {
    }

    public EngineRecursionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
