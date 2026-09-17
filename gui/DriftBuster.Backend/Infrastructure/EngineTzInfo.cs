namespace DriftBuster.Backend.Infrastructure;

/// <summary>The two <c>datetime.tzinfo</c> kinds the engine uses: <see cref="EngineFixedOffset"/> (<c>timezone</c>) and <see cref="EngineZoneInfo"/>.</summary>
public abstract class EngineTzInfo
{
    /// <summary><c>tzinfo.utcoffset(dt)</c> in microseconds for a datetime whose fields are local to this zone.</summary>
    public abstract long UtcOffset(EngineDateTime moment);

    /// <summary><c>tzinfo.fromutc(dt)</c>: <paramref name="moment"/> holds UTC fields and this zone; the result holds local fields.</summary>
    public abstract EngineDateTime FromUtc(EngineDateTime moment);

    /// <summary>
    /// The name <c>run_profiles_cli._serialise_timezone</c> writes: the <c>ZoneInfo</c> key, or <c>str(timezone)</c> for a fixed offset.
    /// </summary>
    public abstract string Name { get; }
}
