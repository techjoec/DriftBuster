namespace DriftBuster.Backend.Infrastructure;

/// <summary>
/// A Python command's <c>raise SystemExit(message)</c>: the command stops, the console tool prints the message and exits with status 1.
/// </summary>
public sealed class CommandExitException : Exception
{
    public CommandExitException()
        : this("Command failed.")
    {
    }

    public CommandExitException(string message)
        : base(message)
    {
    }

    public CommandExitException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
