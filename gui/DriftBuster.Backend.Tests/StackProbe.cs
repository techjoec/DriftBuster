namespace DriftBuster.Backend.Tests;

/// <summary>Runs code on a thread with a deliberately small stack, so a walk that recurses per nesting level overflows.</summary>
internal static class StackProbe
{
    /// <summary>256 KiB: far below the 1.5 MiB of a thread-pool thread, and a few thousand recursive frames at most.</summary>
    public const int SmallStackBytes = 256 * 1024;

    public static T RunOnSmallStack<T>(Func<T> work)
    {
        T result = default!;
        Exception? failure = null;
        var thread = new Thread(
            () =>
            {
                try
                {
                    result = work();
                }
                catch (Exception exc)
                {
                    failure = exc;
                }
            },
            SmallStackBytes);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            throw new InvalidOperationException("Work on the small-stack thread failed.", failure);
        }

        return result;
    }
}
