namespace DriftBuster.Backend.Infrastructure;

/// <summary>
/// Python's <c>UnicodeDecodeError</c> from a strict UTF-8 decode, with <c>str(exc)</c> as the message
/// (<c>'utf-8' codec can't decode byte 0xff in position 1: invalid start byte</c>).
/// </summary>
public sealed class EngineUnicodeDecodeException : Exception
{
    public EngineUnicodeDecodeException()
        : this("Unicode decode error.")
    {
    }

    public EngineUnicodeDecodeException(string message)
        : base(message)
    {
    }

    public EngineUnicodeDecodeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
