using System.Globalization;

namespace DriftBuster.Backend.Settings;

/// <summary>Collects entries in file order; a key seen again gets " #2", " #3" so every key stays unique.</summary>
internal sealed class SettingsBuilder
{
    // A file with more settings is cut here; SettingsExtractor then compares the whole file by hash so a difference past
    // the cut still shows.
    internal const int MaxEntries = 20000;

    private readonly List<SettingEntry> _entries = [];
    private readonly Dictionary<string, int> _seen = new(StringComparer.Ordinal);

    public bool IsFull => _entries.Count >= MaxEntries;

    /// <summary>True when an entry was refused because the builder was full.</summary>
    public bool Truncated { get; private set; }

    public void Add(string key, string value)
    {
        if (IsFull)
        {
            Truncated = true;
            return;
        }

        var count = _seen.TryGetValue(key, out var previous) ? previous + 1 : 1;
        _seen[key] = count;
        _entries.Add(new SettingEntry(count == 1 ? key : string.Create(CultureInfo.InvariantCulture, $"{key} #{count}"), value));
    }

    // A reader stops walking once full, so reaching the cap counts as cut short too.
    public ExtractedSettings Build(SettingsMode mode) => new(mode, _entries, Truncated || IsFull);
}
