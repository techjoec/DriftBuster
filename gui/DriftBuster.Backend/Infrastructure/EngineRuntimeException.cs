namespace DriftBuster.Backend.Infrastructure;

/// <summary>
/// Python's <c>RuntimeError</c> with Python's message text: raised where the standard library raises it (<c>Path.expanduser</c> when no home
/// directory can be determined). An <see cref="InvalidOperationException"/>, the type the error name mappers already read as
/// <c>RuntimeError</c>.
/// </summary>
public sealed class EngineRuntimeException : InvalidOperationException
{
    public EngineRuntimeException()
    {
    }

    public EngineRuntimeException(string message)
        : base(message)
    {
    }

    public EngineRuntimeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
