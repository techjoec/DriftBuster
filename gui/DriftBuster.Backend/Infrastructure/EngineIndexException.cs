namespace DriftBuster.Backend.Infrastructure;

/// <summary>
/// Python's <c>IndexError</c> raised for an argument, with Python's message text exactly: <see cref="Message"/> carries no
/// " (Parameter '...')" suffix, while <see cref="ArgumentException.ParamName"/> still names the argument.
/// </summary>
public sealed class EngineIndexException : ArgumentOutOfRangeException
{
    private readonly string _message;

    public EngineIndexException()
        : this("Index error.")
    {
    }

    public EngineIndexException(string message)
        : this(paramName: null, message)
    {
    }

    public EngineIndexException(string message, Exception innerException)
        : base(message, innerException)
    {
        _message = message;
    }

    public EngineIndexException(string? paramName, string message)
        : base(paramName, message)
    {
        _message = message;
    }

    public override string Message => _message;
}
