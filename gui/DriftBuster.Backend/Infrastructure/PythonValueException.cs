namespace DriftBuster.Backend.Infrastructure;

/// <summary>
/// Python's <c>ValueError</c> raised for an argument, with Python's message text exactly: <see cref="Message"/> carries no
/// " (Parameter '...')" suffix, while <see cref="ArgumentException.ParamName"/> still names the argument.
/// </summary>
public sealed class PythonValueException : ArgumentException
{
    private readonly string _message;

    public PythonValueException()
        : this("Value error.")
    {
    }

    public PythonValueException(string message)
        : this(message, paramName: null)
    {
    }

    public PythonValueException(string message, Exception innerException)
        : this(message, paramName: null, innerException)
    {
    }

    public PythonValueException(string message, string? paramName, Exception? innerException = null)
        : base(message, paramName, innerException)
    {
        _message = message;
    }

    public override string Message => _message;
}
