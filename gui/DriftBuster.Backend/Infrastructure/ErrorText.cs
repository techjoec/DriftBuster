namespace DriftBuster.Backend.Infrastructure;

/// <summary>Exception text for people: the message without the " (Parameter 'name')" suffix .NET adds to argument errors.</summary>
public static class ErrorText
{
    public static string Plain(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var message = exception.Message;
        if (exception is ArgumentException { ParamName: { Length: > 0 } name })
        {
            message = message.Replace($" (Parameter '{name}')", string.Empty, StringComparison.Ordinal);
        }

        return message;
    }
}
