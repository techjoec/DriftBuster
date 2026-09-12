namespace DriftBuster.Backend.Detection;

/// <summary>An I/O failure that occurred while scanning; the message is "{path}: {reason}".</summary>
public sealed class DetectorIOException : IOException
{
    public DetectorIOException(string path, string reason)
        : base($"{path}: {reason}")
    {
        Path = path;
        Reason = reason;
    }

    public DetectorIOException(string path, string reason, Exception innerException)
        : base($"{path}: {reason}", innerException)
    {
        Path = path;
        Reason = reason;
    }

    public DetectorIOException()
        : this(string.Empty, string.Empty)
    {
    }

    public DetectorIOException(string message)
        : this(string.Empty, message)
    {
    }

    public DetectorIOException(string message, Exception innerException)
        : this(string.Empty, message, innerException)
    {
    }

    public string Path { get; }

    public string Reason { get; }
}
