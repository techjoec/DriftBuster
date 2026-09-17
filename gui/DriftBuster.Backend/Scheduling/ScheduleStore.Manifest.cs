using DriftBuster.Backend.Diff;
using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Models;

namespace DriftBuster.Backend.Scheduling;

/// <summary>
/// The GUI's side of <c>Profiles/schedules.json</c>: <see cref="ListSchedules"/> reads the manifest leniently into
/// <see cref="ScheduleDefinition"/> cards (entries without a name, profile or interval skipped, in manifest order), and
/// <see cref="SaveSchedules"/> writes trimmed cards as <c>{"schedules": [...]}</c> in card order. A card is saved only when the entry it
/// writes passes <see cref="ScheduleSpec.FromDict"/> and registers with a <see cref="ProfileScheduler"/> (no repeated name, a first run in
/// range), as <c>run_profiles_cli</c> requires when it loads the manifest.
/// </summary>
/// <remarks>
/// The manifest is read as <c>_load_schedule_payload</c> reads it (<see cref="LoadSchedulePayload"/>, over <see cref="EngineJson"/>), so every
/// manifest the scheduler reads loads as cards, whatever its nesting, floats or unpaired surrogates, and it is written as
/// <c>json.dumps(payload, indent=2)</c> writes it (a container nested 64 levels deep or more on one line, as without indent), which
/// <c>json.loads</c> reads back to the same values. A card shows each field as the text
/// <c>ScheduleSpec.from_dict</c> reads (<c>str()</c> of the JSON value, a falsy <c>start_at</c> as no start). A card read from the manifest
/// keeps its entry (<see cref="ScheduleDefinition.ManifestEntry"/>): every field whose card text still shows what the entry held is written
/// back as the entry held it (JSON value and type, surrounding whitespace, untrimmed metadata keys), and keys the card does not show are
/// kept, so a load and save leaves the scheduler reading what it read before. The exception is a manifest that is a bare top-level
/// array: it is written in the object form, one container deeper, so a bare array nested to the decoder's limit is refused after the save.
/// </remarks>
public static partial class ScheduleStore
{
    private static readonly char[] CardTagSeparators = [',', ';', '\n'];

    // Containers nested this deep in schedules.json are written on one line: the indented layout of a manifest nested thousands of levels
    // deep (which json.loads and so the scheduler still read) would grow with the square of the depth on every save.
    private const int ManifestIndentDepth = 64;

    private static readonly string[] CardFields = ["name", "profile", "every", "start_at", "window", "tags", "metadata"];

    /// <summary>
    /// The manifest's schedules as GUI cards, in manifest order. The entries are the ones <c>_load_schedule_payload</c> returns: an object's
    /// <c>schedules</c>, or the document itself; a missing file, a falsy value and a string are no schedules.
    /// </summary>
    /// <exception cref="CommandExitException">Text that is not JSON, or a truthy value that is neither an array nor a string (Python's messages).</exception>
    /// <exception cref="EngineValueException">An integer past the decoder's digit limit, which the scheduler cannot read either.</exception>
    /// <exception cref="EngineRecursionException">Containers nested past the decoder's limit, which the scheduler cannot read either.</exception>
    public static ScheduleListResult ListSchedules(string? baseDir, CancellationToken cancellationToken = default)
    {
        var path = ScheduleManifestPath(baseDir);
        if (!File.Exists(path))
        {
            return new ScheduleListResult();
        }

        var cards = new List<ScheduleDefinition>();
        foreach (var entry in LoadSchedulePayload(path))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryParseSchedule(entry, out var schedule))
            {
                schedule.ManifestEntry = entry;
                cards.Add(schedule);
            }
        }

        return new ScheduleListResult { Schedules = cards.ToArray() };
    }

    /// <summary>Writes the cards to the manifest in their order, creating the profiles directory.</summary>
    /// <exception cref="InvalidOperationException">A card without a name, profile or interval.</exception>
    /// <exception cref="ScheduleException">An entry <see cref="ScheduleSpec.FromDict"/> refuses, or a name used twice.</exception>
    /// <exception cref="EngineValueException">A start time or window time <c>fromisoformat</c> or <c>int()</c> refuses, or a window time out of range.</exception>
    /// <exception cref="OverflowException">An interval, window time or start time out of Python's range, or a first run past it.</exception>
    public static void SaveSchedules(IEnumerable<ScheduleDefinition> schedules, string? baseDir, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(schedules);
        var manifestPath = ScheduleManifestPath(baseDir);
        var manifestDirectory = Path.GetDirectoryName(manifestPath);
        if (!string.IsNullOrEmpty(manifestDirectory))
        {
            Directory.CreateDirectory(manifestDirectory);
        }

        var document = new OrderedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["schedules"] = ValidateAndSerialiseSchedules(schedules, cancellationToken),
        };
        var json = Canonicaliser.Dumps(document, indent: true, ensureAscii: true, sortKeys: false, maxIndentDepth: ManifestIndentDepth) + "\n";
        if (!string.Equals(Environment.NewLine, "\n", StringComparison.Ordinal))
        {
            json = json.Replace("\n", Environment.NewLine, StringComparison.Ordinal);
        }

        File.WriteAllText(manifestPath, json, Utf8);
    }

    /// <summary>
    /// The error the GUI shows on a card whose required fields are present: the message of the exception <see cref="SaveSchedules"/> would
    /// raise for the entry it writes, or null when the entry is valid.
    /// </summary>
    public static string? ValidationError(ScheduleDefinition schedule)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        try
        {
            _ = new ProfileScheduler([ScheduleSpec.FromDict(SerialiseSchedule(Normalise(schedule)))]);
            return null;
        }
        catch (Exception exc) when (exc is ArgumentException or OverflowException or InvalidOperationException)
        {
            return exc.Message;
        }
    }

    // _build_schedule_specs, then ProfileScheduler(specs): every entry through from_dict, then each registered in order.
    private static List<object?> ValidateAndSerialiseSchedules(IEnumerable<ScheduleDefinition> schedules, CancellationToken cancellationToken)
    {
        var payload = new List<object?>();
        var specs = new List<ScheduleSpec>();
        foreach (var schedule in schedules)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (schedule is null)
            {
                continue;
            }

            var entry = SerialiseSchedule(Normalise(schedule));
            specs.Add(ScheduleSpec.FromDict(entry));
            payload.Add(entry);
        }

        _ = new ProfileScheduler(specs);
        return payload;
    }

    // The trimmed card, or InvalidOperationException for a missing name, profile or interval.
    private static ScheduleDefinition Normalise(ScheduleDefinition schedule)
    {
        var name = schedule.Name?.Trim();
        var profile = schedule.Profile?.Trim();
        var every = schedule.Every?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidOperationException("Schedule name is required.");
        }

        if (string.IsNullOrWhiteSpace(profile))
        {
            throw new InvalidOperationException($"Schedule '{name}' is missing a profile reference.");
        }

        if (string.IsNullOrWhiteSpace(every))
        {
            throw new InvalidOperationException($"Schedule '{name}' is missing an interval.");
        }

        return new ScheduleDefinition
        {
            Name = name,
            Profile = profile,
            Every = every,
            StartAt = CardStartAt(schedule.StartAt),
            Window = NormaliseWindow(schedule.Window),
            Tags = schedule.Tags ?? [],
            Metadata = schedule.Metadata ?? new Dictionary<string, string>(StringComparer.Ordinal),
            EveryValue = schedule.EveryValue,
            MetadataValues = schedule.MetadataValues,
            ManifestEntry = schedule.ManifestEntry,
        };
    }

    // str(value) of the decoded JSON value, the text from_dict reads.
    private static string EngineText(object? value) => EngineRepr.Str(value);

    // The text a card shows for a metadata value: a str as it is, null as nothing, a bool as True or False, anything else as its JSON text.
    private static string MetadataText(object? value) => value switch
    {
        null => string.Empty,
        string text => text,
        bool flag => flag ? "True" : "False",
        _ => Canonicaliser.Dumps(value, indent: false, ensureAscii: false, sortKeys: false),
    };

    private static bool TryParseSchedule(IReadOnlyDictionary<string, object?> element, out ScheduleDefinition schedule)
    {
        schedule = new ScheduleDefinition();
        if (!element.TryGetValue("name", out var nameValue)
            || !element.TryGetValue("profile", out var profileValue)
            || !element.TryGetValue("every", out var everyValue))
        {
            return false;
        }

        var name = EngineText(nameValue).Trim();
        var profile = EngineText(profileValue).Trim();
        var every = EngineText(everyValue).Trim();
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(profile) || string.IsNullOrWhiteSpace(every))
        {
            return false;
        }

        schedule.Name = name;
        schedule.Profile = profile;
        schedule.Every = every;
        schedule.EveryValue = everyValue is string ? null : everyValue;
        schedule.StartAt = element.TryGetValue("start_at", out var startAt) && EngineBuiltins.IsTruthy(startAt)
            ? CardStartAt(EngineText(startAt))
            : null;
        schedule.Window = ParseScheduleWindow(element);
        ParseScheduleMetadata(element, schedule);
        return true;
    }

    // from_dict reads a window only from a mapping; each bound and the time zone as str().
    private static ScheduleWindowDefinition? ParseScheduleWindow(IReadOnlyDictionary<string, object?> element)
    {
        if (!element.TryGetValue("window", out var windowValue) || windowValue is not IReadOnlyDictionary<string, object?> window)
        {
            return null;
        }

        return NormaliseWindow(new ScheduleWindowDefinition
        {
            Start = window.TryGetValue("start", out var start) ? EngineText(start).Trim() : null,
            End = window.TryGetValue("end", out var end) ? EngineText(end).Trim() : null,
            Timezone = window.TryGetValue("timezone", out var timezone) ? EngineText(timezone).Trim() : null,
        });
    }

    private static void ParseScheduleMetadata(IReadOnlyDictionary<string, object?> element, ScheduleDefinition schedule)
    {
        if (element.TryGetValue("tags", out var tags))
        {
            schedule.Tags = ExtractTags(tags);
        }

        if (element.TryGetValue("metadata", out var metadataValue) && metadataValue is IReadOnlyDictionary<string, object?> mapping)
        {
            var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
            var values = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var (key, value) in mapping)
            {
                metadata[key] = MetadataText(value);
                if (value is not string)
                {
                    values[key] = value;
                }
            }

            schedule.Metadata = metadata;
            schedule.MetadataValues = values.Count > 0 ? values : null;
        }
    }

    private static ScheduleWindowDefinition? NormaliseWindow(ScheduleWindowDefinition? window)
    {
        if (window is null)
        {
            return null;
        }

        var start = string.IsNullOrWhiteSpace(window.Start) ? null : window.Start.Trim();
        var end = string.IsNullOrWhiteSpace(window.End) ? null : window.End.Trim();
        var timezone = string.IsNullOrWhiteSpace(window.Timezone) ? null : window.Timezone.Trim();
        return start is null && end is null && timezone is null
            ? null
            : new ScheduleWindowDefinition { Start = start, End = end, Timezone = timezone };
    }

    private static string? CardStartAt(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static OrderedDictionary<string, object?> SerialiseSchedule(ScheduleDefinition schedule)
    {
        if (schedule.ManifestEntry is { } source && TryParseSchedule(source, out var shown))
        {
            return SerialiseKeepingEntry(schedule, source, shown);
        }

        var entry = new OrderedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["name"] = schedule.Name,
            ["profile"] = schedule.Profile,
            ["every"] = CardEvery(schedule),
        };

        foreach (var field in CardFields.Skip(3))
        {
            if (CardValue(schedule, field) is { } value)
            {
                entry[field] = value;
            }
        }

        return entry;
    }

    // The entry the card was read from, in its key order, with each card field the card still shows as read kept as the entry holds it and
    // each edited field written from the card (dropped when the card clears it); fields the entry lacked are appended from the card.
    private static OrderedDictionary<string, object?> SerialiseKeepingEntry(
        ScheduleDefinition schedule,
        IReadOnlyDictionary<string, object?> source,
        ScheduleDefinition shown)
    {
        var entry = new OrderedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in source)
        {
            if (!CardFields.Contains(key, StringComparer.Ordinal) || FieldUnchanged(schedule, shown, key))
            {
                entry[key] = value;
            }
            else if (CardValue(schedule, key) is { } edited)
            {
                entry[key] = edited;
            }
        }

        foreach (var field in CardFields.Where(field => !source.ContainsKey(field)))
        {
            if (CardValue(schedule, field) is { } value)
            {
                entry[field] = value;
            }
        }

        return entry;
    }

    // The value the card writes for one field, or null when the card leaves the field out.
    private static object? CardValue(ScheduleDefinition schedule, string field)
    {
        switch (field)
        {
            case "name":
                return schedule.Name;
            case "profile":
                return schedule.Profile;
            case "every":
                return CardEvery(schedule);
            case "start_at":
                return schedule.StartAt;
            case "window":
                return SerialiseScheduleWindow(schedule.Window);
            case "tags":
                var cleanedTags = CleanTags(schedule.Tags);
                return cleanedTags.Length > 0 ? cleanedTags.Cast<object?>().ToList() : null;
            default:
                return SerialiseMetadata(schedule);
        }
    }

    // The interval read from the manifest while the card still shows its str() text, so a number keeps its JSON type; otherwise the card text.
    private static object CardEvery(ScheduleDefinition schedule)
        => schedule.EveryValue is { } value && string.Equals(EngineText(value).Trim(), schedule.Every, StringComparison.Ordinal)
            ? value
            : schedule.Every;

    // True when the card field shows what the manifest entry held, compared as the Profiles tab normalises what it shows.
    private static bool FieldUnchanged(ScheduleDefinition card, ScheduleDefinition shown, string field) => field switch
    {
        "name" => string.Equals(card.Name?.Trim(), shown.Name, StringComparison.Ordinal),
        "profile" => string.Equals(card.Profile?.Trim(), shown.Profile, StringComparison.Ordinal),
        "every" => string.Equals(card.Every?.Trim(), shown.Every, StringComparison.Ordinal),
        "start_at" => string.Equals(CardStartAt(card.StartAt), shown.StartAt, StringComparison.Ordinal),
        "window" => SameWindow(NormaliseWindow(card.Window), shown.Window),
        "tags" => CardTags(card.Tags).SequenceEqual(CardTags(shown.Tags), StringComparer.Ordinal),
        _ => CardMetadata(card.Metadata).SequenceEqual(CardMetadata(shown.Metadata)),
    };

    private static bool SameWindow(ScheduleWindowDefinition? left, ScheduleWindowDefinition? right)
        => (left is null && right is null)
            || (left is not null && right is not null
                && string.Equals(left.Start, right.Start, StringComparison.Ordinal)
                && string.Equals(left.End, right.End, StringComparison.Ordinal)
                && string.Equals(left.Timezone, right.Timezone, StringComparison.Ordinal));

    // The tags as the Profiles tab edits them: joined with ", ", split on its separators, trimmed, blanks dropped, repeats (ignoring case) dropped.
    private static string[] CardTags(string[]? tags)
        => CleanTags(string.Join(", ", tags ?? []).Split(CardTagSeparators, StringSplitOptions.RemoveEmptyEntries));

    // The metadata as the Profiles tab edits it: blank keys dropped, keys and values trimmed, the first of a repeated key kept.
    private static List<KeyValuePair<string, string>> CardMetadata(IDictionary<string, string>? metadata)
    {
        var pairs = new List<KeyValuePair<string, string>>();
        foreach (var (key, value) in metadata ?? new Dictionary<string, string>(StringComparer.Ordinal))
        {
            if (!string.IsNullOrWhiteSpace(key) && !pairs.Exists(pair => string.Equals(pair.Key, key.Trim(), StringComparison.Ordinal)))
            {
                pairs.Add(KeyValuePair.Create(key.Trim(), value?.Trim() ?? string.Empty));
            }
        }

        return pairs;
    }

    // Each metadata value read from the manifest while the card still shows its text keeps its JSON value (a number, a boolean, null, a list
    // or an object); an edited value is written as the card's text.
    private static OrderedDictionary<string, object?>? SerialiseMetadata(ScheduleDefinition schedule)
    {
        if (schedule.Metadata is not { Count: > 0 })
        {
            return null;
        }

        var metadata = new OrderedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var pair in schedule.Metadata)
        {
            if (string.IsNullOrWhiteSpace(pair.Key))
            {
                continue;
            }

            var text = pair.Value ?? string.Empty;
            metadata[pair.Key.Trim()] = schedule.MetadataValues is { } values && values.TryGetValue(pair.Key, out var kept)
                && string.Equals(MetadataText(kept), text, StringComparison.Ordinal)
                    ? kept
                    : text;
        }

        return metadata.Count > 0 ? metadata : null;
    }

    private static OrderedDictionary<string, object?>? SerialiseScheduleWindow(ScheduleWindowDefinition? window)
    {
        if (window is null)
        {
            return null;
        }

        var windowPayload = new OrderedDictionary<string, object?>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(window.Start))
        {
            windowPayload["start"] = window.Start;
        }

        if (!string.IsNullOrWhiteSpace(window.End))
        {
            windowPayload["end"] = window.End;
        }

        if (!string.IsNullOrWhiteSpace(window.Timezone))
        {
            windowPayload["timezone"] = window.Timezone;
        }

        return windowPayload.Count > 0 ? windowPayload : null;
    }

    private static string[] CleanTags(IEnumerable<string?>? tags)
        => (tags ?? [])
            .Select(tag => tag?.Trim())
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Select(tag => tag!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    // from_dict's tags: each item's str() of a list, str() of any other truthy value, nothing for a falsy one; blanks dropped.
    private static string[] ExtractTags(object? tags)
    {
        if (tags is List<object?> items)
        {
            return CleanTags(items.Select(EngineText));
        }

        return EngineBuiltins.IsTruthy(tags) ? CleanTags([EngineText(tags)]) : [];
    }

    private static string ScheduleManifestPath(string? baseDir)
        => Path.Combine(string.IsNullOrWhiteSpace(baseDir) ? Environment.CurrentDirectory : baseDir, "Profiles", "schedules.json");
}
