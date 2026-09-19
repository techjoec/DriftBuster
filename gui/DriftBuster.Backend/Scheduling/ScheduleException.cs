namespace DriftBuster.Backend.Scheduling;

/// <summary>An invalid schedule, schedule file or scheduler request; the message says which field and why.</summary>
public sealed class ScheduleException : Exception
{
    public ScheduleException()
        : base("Invalid schedule.")
    {
    }

    public ScheduleException(string message)
        : base(message)
    {
    }

    public ScheduleException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
