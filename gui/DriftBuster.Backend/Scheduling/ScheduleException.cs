namespace DriftBuster.Backend.Scheduling;

/// <summary>Invalid schedule input. The message is the refusal alone, without a parameter name.</summary>
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
