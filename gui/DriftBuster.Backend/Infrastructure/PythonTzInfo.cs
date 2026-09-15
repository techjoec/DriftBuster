namespace DriftBuster.Backend.Infrastructure;

/// <summary>The two <c>datetime.tzinfo</c> kinds the port uses: <see cref="PythonFixedOffset"/> (<c>timezone</c>) and <see cref="PythonZoneInfo"/>.</summary>
public abstract class PythonTzInfo
{
    /// <summary><c>tzinfo.utcoffset(dt)</c> in microseconds for a datetime whose fields are local to this zone.</summary>
    public abstract long UtcOffset(PythonDateTime moment);

    /// <summary><c>tzinfo.fromutc(dt)</c>: <paramref name="moment"/> holds UTC fields and this zone; the result holds local fields.</summary>
    public abstract PythonDateTime FromUtc(PythonDateTime moment);

    /// <summary>
    /// The name <c>run_profiles_cli._serialise_timezone</c> writes: the <c>ZoneInfo</c> key, or <c>str(timezone)</c> for a fixed offset.
    /// </summary>
    public abstract string Name { get; }
}
