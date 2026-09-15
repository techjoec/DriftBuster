namespace DriftBuster.Backend.Infrastructure;

/// <summary>Python's <c>RecursionError</c>, with Python's message text.</summary>
public sealed class PythonRecursionException : InvalidOperationException
{
    public PythonRecursionException()
        : this("maximum recursion depth exceeded")
    {
    }

    public PythonRecursionException(string message)
        : base(message)
    {
    }

    public PythonRecursionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
