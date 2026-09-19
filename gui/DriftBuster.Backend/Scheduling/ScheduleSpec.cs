using DriftBuster.Backend.Models;

namespace DriftBuster.Backend.Scheduling;

/// <summary>A validated <see cref="ScheduleDefinition"/>: a named schedule running a profile every <see cref="Interval"/>.</summary>
public sealed record ScheduleSpec(
    string Name,
    string Profile,
    TimeSpan Interval,
    DateTimeOffset? StartAt,
    ScheduleWindow? Window,
    IReadOnlyList<string> Tags,
    IReadOnlyDictionary<string, string> Metadata)
{
    /// <summary>The definition checked field by field; <see cref="ScheduleException"/> names the first invalid one.</summary>
    public static ScheduleSpec From(ScheduleDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var name = Required(definition.Name, "name");
        var profile = Required(definition.Profile, "profile");
        var interval = Field("every", () => ScheduleParsing.ParseInterval(definition.Every));
        DateTimeOffset? startAt = string.IsNullOrWhiteSpace(definition.StartAt)
            ? null
            : Field("start_at", () => ScheduleParsing.ParseTimestamp(definition.StartAt));
        var window = definition.Window is { } windowDefinition ? Field("window", () => ScheduleWindow.From(windowDefinition)) : null;
        var tags = definition.Tags.Select(tag => tag.Trim()).Where(tag => tag.Length > 0).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        return new ScheduleSpec(name, profile, interval, startAt, window, tags, definition.Metadata);
    }

    /// <summary>The reference in UTC, aligned to the window when there is one.</summary>
    public DateTimeOffset AlignTo(DateTimeOffset reference)
    {
        var candidate = reference.ToUniversalTime();
        return Window is null ? candidate : Window.Align(candidate);
    }

    /// <summary>The first run: the start, else <paramref name="now"/>, aligned.</summary>
    public DateTimeOffset InitialRun(DateTimeOffset now) => AlignTo(StartAt ?? now);

    /// <summary>The run after <paramref name="moment"/>: the moment plus the interval, aligned.</summary>
    public DateTimeOffset NextAfter(DateTimeOffset moment) => AlignTo(moment.ToUniversalTime() + Interval);

    private static string Required(string? value, string field)
        => string.IsNullOrWhiteSpace(value) ? throw new ScheduleException($"{field}: required.") : value.Trim();

    private static T Field<T>(string field, Func<T> parse)
    {
        try
        {
            return parse();
        }
        catch (ScheduleException exc)
        {
            throw new ScheduleException($"{field}: {exc.Message}", exc);
        }
    }
}
