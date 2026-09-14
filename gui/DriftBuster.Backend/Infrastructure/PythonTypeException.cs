namespace DriftBuster.Backend.Infrastructure;

/// <summary>
/// Python's <c>TypeError</c> raised for an argument, with Python's message text exactly: <see cref="Message"/> carries no
/// " (Parameter '...')" suffix, while <see cref="ArgumentException.ParamName"/> still names the argument.
/// </summary>
public sealed class PythonTypeException : ArgumentException
{
    private readonly string _message;

    public PythonTypeException()
        : this("Type error.")
    {
    }

    public PythonTypeException(string message)
        : this(message, paramName: null)
    {
    }

    public PythonTypeException(string message, Exception innerException)
        : this(message, paramName: null, innerException)
    {
    }

    public PythonTypeException(string message, string? paramName, Exception? innerException = null)
        : base(message, paramName, innerException)
    {
        _message = message;
    }

    public override string Message => _message;
}
