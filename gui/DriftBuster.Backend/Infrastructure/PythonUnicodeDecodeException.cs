namespace DriftBuster.Backend.Infrastructure;

/// <summary>
/// Python's <c>UnicodeDecodeError</c> from a strict UTF-8 decode, with <c>str(exc)</c> as the message
/// (<c>'utf-8' codec can't decode byte 0xff in position 1: invalid start byte</c>).
/// </summary>
public sealed class PythonUnicodeDecodeException : Exception
{
    public PythonUnicodeDecodeException()
        : this("Unicode decode error.")
    {
    }

    public PythonUnicodeDecodeException(string message)
        : base(message)
    {
    }

    public PythonUnicodeDecodeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
