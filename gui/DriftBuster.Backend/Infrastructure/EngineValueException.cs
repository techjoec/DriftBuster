namespace DriftBuster.Backend.Infrastructure;

/// <summary>
/// Python's <c>ValueError</c> raised for an argument, with Python's message text exactly: <see cref="Message"/> carries no
/// " (Parameter '...')" suffix, while <see cref="ArgumentException.ParamName"/> still names the argument.
/// </summary>
public sealed class EngineValueException : ArgumentException
{
    private readonly string _message;

    public EngineValueException()
        : this("Value error.")
    {
    }

    public EngineValueException(string message)
        : this(message, paramName: null)
    {
    }

    public EngineValueException(string message, Exception innerException)
        : this(message, paramName: null, innerException)
    {
    }

    public EngineValueException(string message, string? paramName, Exception? innerException = null)
        : base(message, paramName, innerException)
    {
        _message = message;
    }

    public override string Message => _message;
}
