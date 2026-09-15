namespace DriftBuster.Backend.Scheduling;

/// <summary><c>scheduler.ScheduleError</c> (a <c>ValueError</c>): invalid schedule input, with Python's message text exactly.</summary>
public sealed class ScheduleException : ArgumentException
{
    private readonly string _message;

    public ScheduleException()
        : this("Invalid schedule.")
    {
    }

    public ScheduleException(string message)
        : this(message, innerException: null)
    {
    }

    public ScheduleException(string message, Exception? innerException)
        : base(message, paramName: null, innerException)
    {
        _message = message;
    }

    public override string Message => _message;
}
