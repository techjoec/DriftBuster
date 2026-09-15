namespace DriftBuster.Backend.Infrastructure;

/// <summary>Python's <c>AttributeError</c> (<c>'list' object has no attribute 'get'</c>) with Python's message text.</summary>
public sealed class PythonAttributeException : Exception
{
    public PythonAttributeException()
        : this("Attribute error.")
    {
    }

    public PythonAttributeException(string message)
        : base(message)
    {
    }

    public PythonAttributeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
