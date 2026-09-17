namespace DriftBuster.Backend.Infrastructure;

/// <summary>
/// Python's <c>RuntimeError</c> with Python's message text: raised where the ported code raises it (<c>Path.expanduser</c> when no home
/// directory can be determined). An <see cref="InvalidOperationException"/>, the type the error name mappers already read as
/// <c>RuntimeError</c>.
/// </summary>
public sealed class PythonRuntimeException : InvalidOperationException
{
    public PythonRuntimeException()
    {
    }

    public PythonRuntimeException(string message)
        : base(message)
    {
    }

    public PythonRuntimeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
