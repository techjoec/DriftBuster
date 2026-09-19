namespace DriftBuster.Backend.Profiles.Detection;

/// <summary>An invalid detection profile store, summary or hunt file; the message names the file, the JSON path or the entry.</summary>
public sealed class DetectionProfileException : Exception
{
    public DetectionProfileException()
        : base("Invalid detection profile.")
    {
    }

    public DetectionProfileException(string message)
        : base(message)
    {
    }

    public DetectionProfileException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
