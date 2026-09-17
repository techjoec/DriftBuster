namespace DriftBuster.Backend.Infrastructure;

/// <summary>
/// Python's <c>NotImplementedError</c> with Python's message text: raised where the standard library raises it (<c>Path.glob</c> on an
/// anchored pattern). A <see cref="NotSupportedException"/>, so callers refusing unsupported input keep catching it.
/// </summary>
public sealed class EngineNotImplementedException : NotSupportedException
{
    public EngineNotImplementedException()
    {
    }

    public EngineNotImplementedException(string message)
        : base(message)
    {
    }

    public EngineNotImplementedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
