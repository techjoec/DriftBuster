namespace DriftBuster.Backend.Infrastructure;

/// <summary>
/// Python's <c>NotImplementedError</c> with Python's message text: raised where the ported code raises it (<c>Path.glob</c> on an
/// anchored pattern). A <see cref="NotSupportedException"/>, so callers refusing unsupported input keep catching it.
/// </summary>
public sealed class PythonNotImplementedException : NotSupportedException
{
    public PythonNotImplementedException()
    {
    }

    public PythonNotImplementedException(string message)
        : base(message)
    {
    }

    public PythonNotImplementedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
