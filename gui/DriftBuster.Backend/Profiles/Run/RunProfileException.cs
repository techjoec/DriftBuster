namespace DriftBuster.Backend.Profiles.Run;

/// <summary>An invalid run profile, profile file or run; the message names the file, field or source and why.</summary>
public sealed class RunProfileException : Exception
{
    public RunProfileException()
        : base("Invalid run profile.")
    {
    }

    public RunProfileException(string message)
        : base(message)
    {
    }

    public RunProfileException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
