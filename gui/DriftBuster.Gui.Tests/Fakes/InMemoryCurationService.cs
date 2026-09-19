using DriftBuster.Backend.Curation;
using DriftBuster.Backend.History;
using DriftBuster.Backend.Models;
using DriftBuster.Gui.Services;

namespace DriftBuster.Gui.Tests.Fakes;

/// <summary>Curation kept in memory, with history answered from lists the test fills.</summary>
internal sealed class InMemoryCurationService : ICurationService
{
    public CurationDocument Document { get; private set; } = new();

    public string? LoadError { get; init; }

    public int Saves { get; private set; }

    public List<(SettingsComparison Comparison, string HostSetId)> Recorded { get; } = [];

    public List<HistoryEntry> Entries { get; } = [];

    public bool Cleared { get; private set; }

    public string? Exported { get; private set; }

    public event EventHandler? Changed;

    public void Update(Func<CurationDocument, CurationDocument> change)
    {
        Document = change(Document);
        Saves++;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Export(string path) => Exported = path;

    public void Import(string path, bool replace) => Update(current => replace ? new CurationDocument() : current);

    public Task RecordHistoryAsync(SettingsComparison comparison, string hostSetId)
    {
        Recorded.Add((comparison, hostSetId));
        return Task.CompletedTask;
    }

    public IReadOnlyList<HistoryEntry> SettingHistory(string path, string key) =>
        Entries.Where(entry => string.Equals(entry.Path, path, StringComparison.OrdinalIgnoreCase) && string.Equals(entry.Key, key, StringComparison.Ordinal)).ToList();

    public IReadOnlyList<HistoryEntry> WhereSettingIsSet(string key) => Entries.Where(entry => string.Equals(entry.Key, key, StringComparison.Ordinal)).ToList();

    public IReadOnlyList<HistoryEntry> WhereValueAppears(string valueHash) => Entries.Where(entry => string.Equals(entry.ValueHash, valueHash, StringComparison.Ordinal)).ToList();

    public HistoryStats HistoryStats() => new(Recorded.Count, Recorded.Count, 0);

    public void ClearHistory() => Cleared = true;
}
