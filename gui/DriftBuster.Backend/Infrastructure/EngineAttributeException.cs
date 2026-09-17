namespace DriftBuster.Backend.Infrastructure;

/// <summary>Python's <c>AttributeError</c> (<c>'list' object has no attribute 'get'</c>) with Python's message text.</summary>
public sealed class EngineAttributeException : Exception
{
    public EngineAttributeException()
        : this("Attribute error.")
    {
    }

    public EngineAttributeException(string message)
        : base(message)
    {
    }

    public EngineAttributeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
