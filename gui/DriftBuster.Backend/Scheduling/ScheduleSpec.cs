using System.Collections.ObjectModel;

using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Profiles.Run;

namespace DriftBuster.Backend.Scheduling;

/// <summary>
/// <c>scheduler.ScheduleSpec</c>: a named schedule running a profile every <see cref="Interval"/>, from an optional start, inside an
/// optional daily window, with tags and metadata (values in the <see cref="EngineJson"/> domain) and an optional profile loader.
/// </summary>
public sealed class ScheduleSpec
{
    /// <summary>The dataclass constructor with <c>__post_init__</c>: a positive interval, then a name and a profile that are not blank.</summary>
    public ScheduleSpec(
        string name,
        string profile,
        TimeSpan interval,
        DateTimeOffset? startAt = null,
        ScheduleWindow? window = null,
        IReadOnlyList<string>? tags = null,
        IReadOnlyDictionary<string, object?>? metadata = null,
        Func<string, RunProfile>? loader = null)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(profile);
        if (interval <= TimeSpan.Zero)
        {
            throw new ScheduleException("Interval must be positive.");
        }

        if (EngineText.Strip(name).Length == 0)
        {
            throw new ScheduleException("Schedule name must not be empty.");
        }

        if (EngineText.Strip(profile).Length == 0)
        {
            throw new ScheduleException("Profile reference must not be empty.");
        }

        Name = name;
        Profile = profile;
        Interval = interval;
        StartAt = startAt?.ToUniversalTime();
        Window = window;
        Tags = (tags ?? []).ToList().AsReadOnly();
        Metadata = new ReadOnlyDictionary<string, object?>(new OrderedDictionary<string, object?>(metadata ?? new OrderedDictionary<string, object?>(StringComparer.Ordinal), StringComparer.Ordinal));
        Loader = loader;
    }

    public string Name { get; }

    public string Profile { get; }

    public TimeSpan Interval { get; }

    /// <summary>The first-run anchor, in UTC.</summary>
    public DateTimeOffset? StartAt { get; }

    public ScheduleWindow? Window { get; }

    public IReadOnlyList<string> Tags { get; }

    public IReadOnlyDictionary<string, object?> Metadata { get; }

    public Func<string, RunProfile>? Loader { get; }

    /// <summary>
    /// <c>ScheduleSpec.from_dict(payload, profile_loader=...)</c>: <c>str()</c> of <c>name</c> and <c>profile</c> and the raw <c>every</c>
    /// (all required), a truthy <c>start_at</c> through <see cref="ScheduleParsing.ParseIsoTimestamp"/> (UTC), a mapping
    /// <c>window</c>, <c>tags</c> (a list becomes its stripped non-empty <c>str()</c> items sorted by code point, any other truthy value
    /// one stripped item), a mapping <c>metadata</c> (anything else raises), and finally the interval.
    /// </summary>
    public static ScheduleSpec FromDict(IReadOnlyDictionary<string, object?> payload, Func<string, RunProfile>? profileLoader = null)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (!payload.TryGetValue("name", out var name) || !payload.TryGetValue("profile", out var profile) || !payload.TryGetValue("every", out var every))
        {
            throw new ScheduleException("Schedule entries require name, profile, and every");
        }

        var startAtRaw = payload.GetValueOrDefault("start_at");
        DateTimeOffset? startAt = EngineBuiltins.IsTruthy(startAtRaw)
            ? ScheduleParsing.ParseIsoTimestamp(EngineRepr.Str(startAtRaw))
            : null;
        var window = payload.GetValueOrDefault("window") is IReadOnlyDictionary<string, object?> windowPayload
            ? ScheduleWindow.FromDict(windowPayload)
            : null;
        var tags = TagsFrom(payload.TryGetValue("tags", out var tagsRaw) ? tagsRaw : new List<object?>());
        var metadataRaw = payload.TryGetValue("metadata", out var metadataValue) ? metadataValue : new OrderedDictionary<string, object?>(StringComparer.Ordinal);
        if (metadataRaw is not IReadOnlyDictionary<string, object?> metadata)
        {
            throw new ScheduleException("Metadata must be a mapping when provided.");
        }

        var interval = ScheduleParsing.ParseInterval(every);
        return new ScheduleSpec(EngineRepr.Str(name), EngineRepr.Str(profile), interval, startAt, window, tags, metadata, profileLoader);
    }

    /// <summary>The reference in UTC, aligned to the window when there is one.</summary>
    public DateTimeOffset AlignTo(DateTimeOffset reference)
    {
        var candidate = reference.ToUniversalTime();
        return Window is null ? candidate : Window.Align(candidate);
    }

    /// <summary>The first run: the start, else the reference, else now, aligned.</summary>
    public DateTimeOffset InitialRun(DateTimeOffset? reference = null) => AlignTo(StartAt ?? reference ?? IsoTimestamp.UtcNow());

    /// <summary>The run after <paramref name="moment"/>: the moment in UTC plus the interval, aligned.</summary>
    public DateTimeOffset NextAfter(DateTimeOffset moment) => AlignTo(moment.ToUniversalTime() + Interval);

    /// <summary><c>spec.load_profile()</c>.</summary>
    public RunProfile LoadProfile()
        => Loader is null ? throw new ScheduleException("Profile loader not configured for this schedule") : Loader(Profile);

    private static List<string> TagsFrom(object? raw)
    {
        if (raw is List<object?> items)
        {
            var tags = items.Select(tag => EngineText.Strip(EngineRepr.Str(tag))).Where(tag => tag.Length > 0).ToList();
            tags.Sort(PathText.CompareCodePoints);
            return tags;
        }

        return EngineBuiltins.IsTruthy(raw) ? [EngineText.Strip(EngineRepr.Str(raw))] : [];
    }
}
