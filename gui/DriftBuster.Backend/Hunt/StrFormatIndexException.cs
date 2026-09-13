namespace DriftBuster.Backend.Hunt;

/// <summary>
/// Python's <c>IndexError</c> from <c>str.format</c> (a positional field, or a <c>[n]</c> index past the end of the string),
/// with Python's message text exactly: unlike <see cref="ArgumentOutOfRangeException"/>, no parameter name is appended.
/// </summary>
public sealed class StrFormatIndexException : Exception
{
    public StrFormatIndexException()
    {
    }

    public StrFormatIndexException(string message)
        : base(message)
    {
    }

    public StrFormatIndexException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
