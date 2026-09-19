using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using DriftBuster.Backend.Curation;
using DriftBuster.Backend.History;
using DriftBuster.Backend.Models;

namespace DriftBuster.Gui.Services
{
    /// <summary>
    /// Curation kept in <c>curation.json</c> and history in <c>history.db</c>, both under the data root unless given other paths.
    /// A curation file that cannot be read raises <see cref="InvalidDataException"/> from the constructor and is never replaced.
    /// </summary>
    public sealed class CurationService : ICurationService
    {
        private static readonly Lazy<CurationService> SharedInstance = new(() => new CurationService(CurationStore.DefaultPath, HistoryStore.DefaultPath));

        private readonly string _curationPath;
        private readonly HistoryStore _history;
        private readonly Lock _gate = new();

        public CurationService(string curationPath, string historyPath)
        {
            _curationPath = curationPath ?? throw new ArgumentNullException(nameof(curationPath));
            _history = new HistoryStore(historyPath ?? throw new ArgumentNullException(nameof(historyPath)));
            Document = CurationStore.Load(_curationPath);
        }

        /// <summary>The service over the data root, shared by every view.</summary>
        public static CurationService Shared => SharedInstance.Value;

        public CurationDocument Document { get; private set; }

        public event EventHandler? Changed;

        public void Update(Func<CurationDocument, CurationDocument> change)
        {
            ArgumentNullException.ThrowIfNull(change);
            lock (_gate)
            {
                Document = change(Document);
                CurationStore.Save(Document, _curationPath);
            }

            Changed?.Invoke(this, EventArgs.Empty);
        }

        public void Export(string path) => CurationStore.Save(Document, path);

        public void Import(string path, bool replace)
        {
            var incoming = CurationStore.Load(path);
            Update(current => replace ? incoming : CurationStore.Merge(current, incoming));
        }

        public Task RecordHistoryAsync(SettingsComparison comparison, string hostSetId) =>
            Task.Run(() => _history.Record(comparison, hostSetId, DateTimeOffset.UtcNow));

        public IReadOnlyList<HistoryEntry> SettingHistory(string path, string key) => _history.SettingHistory(path, key);

        public IReadOnlyList<HistoryEntry> WhereSettingIsSet(string key) => _history.WhereSettingIsSet(key);

        public IReadOnlyList<HistoryEntry> WhereValueAppears(string valueHash) => _history.WhereValueAppears(valueHash);

        public HistoryStats HistoryStats() => _history.Stats();

        public void ClearHistory() => _history.Clear();
    }
}
