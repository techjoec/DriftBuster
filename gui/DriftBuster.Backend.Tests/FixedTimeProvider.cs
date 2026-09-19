namespace DriftBuster.Backend.Tests;

/// <summary>A clock that always reads <paramref name="now"/>.</summary>
internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}
