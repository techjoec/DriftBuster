using System.Collections.Concurrent;

namespace DriftBuster.Backend.Infrastructure;

/// <summary>
/// <c>zoneinfo.ZoneInfo(key)</c>. On Unix the zone is read from its TZif file as CPython's C implementation reads it (<see cref="TzifZone"/>):
/// the explicit transitions, then the POSIX TZ footer rule, with offsets in whole seconds and zoneinfo's gap and fold rules. On Windows,
/// which has no TZif files, the offsets come from the runtime's <see cref="TimeZoneInfo"/> under the IANA id (see
/// <c>EngineZoneInfo.Runtime.cs</c>).
/// </summary>
public sealed partial class EngineZoneInfo : EngineTzInfo
{
    private const long MicrosecondsPerSecond = 1_000_000;

    // (date(1970, 1, 1).toordinal() - 1) * 86400: seconds from 0001-01-01T00:00:00 to the epoch.
    private const long EpochFieldSeconds = 719_162L * 86_400;

    private static readonly ConcurrentDictionary<string, EngineZoneInfo> Cache = new(StringComparer.Ordinal);

    private readonly TzifZone? _tzif;

    private EngineZoneInfo(string key, TzifZone? tzif, TimeZoneInfo? runtime)
    {
        Key = key;
        _tzif = tzif;
        _runtime = runtime;
    }

    /// <summary><c>zone.key</c>.</summary>
    public string Key { get; }

    public override string Name => Key;

    /// <summary>
    /// <c>ZoneInfo(key)</c>, cached per key as zoneinfo caches instances. The key must be a normalised relative path inside the time zone
    /// directory (<c>ValueError</c> otherwise). On Unix it must name a TZif file under <c>TZDIR</c> or <c>/usr/share/zoneinfo</c> with the
    /// exact spelling (<see cref="TimeZoneNotFoundException"/>, zoneinfo's <c>ZoneInfoNotFoundError</c>, otherwise), whose data zoneinfo
    /// accepts (<see cref="EngineValueException"/> otherwise); on Windows it must be an IANA id the runtime resolves.
    /// </summary>
    public static EngineZoneInfo Create(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (Cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        ValidateKey(key);
        var zone = OperatingSystem.IsWindows() ? new EngineZoneInfo(key, tzif: null, FindRuntimeZone(key)) : new EngineZoneInfo(key, LoadTzif(key), runtime: null);
        return Cache.GetOrAdd(key, zone);
    }

    /// <summary><c>ZoneInfo.utcoffset(dt)</c> in microseconds for a datetime whose fields are local to this zone.</summary>
    public override long UtcOffset(EngineDateTime moment)
    {
        ArgumentNullException.ThrowIfNull(moment);
        var seconds = _tzif is { } tzif
            ? tzif.UtcOffset(moment.FieldSeconds - EpochFieldSeconds, moment.Fold, moment.Year)
            : RuntimeUtcOffset(moment);
        return seconds * MicrosecondsPerSecond;
    }

    /// <summary><c>ZoneInfo.fromutc(dt)</c>: the fields plus the offset in force, with fold 1 inside the repeated interval after a backward shift.</summary>
    public override EngineDateTime FromUtc(EngineDateTime moment)
    {
        ArgumentNullException.ThrowIfNull(moment);
        if (_tzif is null)
        {
            return RuntimeFromUtc(moment);
        }

        var (offset, fold) = _tzif.FromUtc(moment.FieldSeconds - EpochFieldSeconds, moment.Year);
        var shifted = moment.AddMicroseconds(offset * MicrosecondsPerSecond);
        return fold ? shifted.WithFold(1) : shifted;
    }

    // _validate_tzfile_path: not absolute, unchanged in length by normpath, and inside the base directory once joined.
    private static void ValidateKey(string key)
    {
        if (OperatingSystem.IsWindows() ? Path.IsPathRooted(key) : key.StartsWith('/'))
        {
            throw new EngineValueException($"ZoneInfo keys may not be absolute paths, got: {key}", nameof(key));
        }

        var normalised = EngineOsPath.NormPath(OperatingSystem.IsWindows() ? key.Replace('/', '\\') : key);
        if (normalised.Length != key.Length)
        {
            throw new EngineValueException($"ZoneInfo keys must be normalized relative paths, got: {key}", nameof(key));
        }

        if (!EngineOsPath.NormPath("_/" + normalised).StartsWith("_/", StringComparison.Ordinal))
        {
            throw new EngineValueException($"ZoneInfo keys must refer to subdirectories of TZPATH, got: {key}", nameof(key));
        }
    }

    // find_tzfile then load_data: a regular file under the zone directory, read and parsed; a file that starts with anything but "TZif", or
    // cannot be read, is not found (the key lookup); data zoneinfo refuses raises ValueError (a UnicodeDecodeError is one).
    private static TzifZone LoadTzif(string key)
    {
        byte[] data;
        try
        {
            data = key.Contains('\0', StringComparison.Ordinal) ? [] : ReadZoneFile(key);
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
        {
            throw NotFound(key);
        }

        if (data.Length < 4 || !data.AsSpan(0, 4).SequenceEqual("TZif"u8))
        {
            throw NotFound(key);
        }

        try
        {
            return TzifZone.Load(data);
        }
        catch (EngineUnicodeDecodeException exc)
        {
            throw new EngineValueException(exc.Message, nameof(key), exc);
        }
    }

    private static byte[] ReadZoneFile(string key)
    {
        var directory = Environment.GetEnvironmentVariable("TZDIR") is { Length: > 0 } tzdir ? tzdir : "/usr/share/zoneinfo";
        var path = EngineOsPath.Join(directory, key);
        return EnginePath.IsFile(path) ? File.ReadAllBytes(path) : [];
    }

    private static TimeZoneNotFoundException NotFound(string key) => new($"No time zone found with key {key}");
}
