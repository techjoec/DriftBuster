using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using DriftBuster.Backend.Curation;
using DriftBuster.Backend.History;
using DriftBuster.Backend.Models;

namespace DriftBuster.Gui.Services
{
    /// <summary>The user's saved curation (groups, rules, choices, review list) and the scan history, for the GUI.</summary>
    public interface ICurationService
    {
        CurationDocument Document { get; }

        /// <summary>Why the saved curation could not be read; while set, changes are kept in memory and never saved.</summary>
        string? LoadError { get; }

        /// <summary>Raised after every change to <see cref="Document"/>.</summary>
        event EventHandler? Changed;

        /// <summary>Applies a change to the document and saves it.</summary>
        void Update(Func<CurationDocument, CurationDocument> change);

        void Export(string path);

        /// <summary>Merges (or, with <paramref name="replace"/>, replaces with) the curation in <paramref name="path"/>.</summary>
        void Import(string path, bool replace);

        Task RecordHistoryAsync(SettingsComparison comparison, string hostSetId);

        IReadOnlyList<HistoryEntry> SettingHistory(string path, string key);

        IReadOnlyList<HistoryEntry> WhereSettingIsSet(string key);

        IReadOnlyList<HistoryEntry> WhereValueAppears(string valueHash);

        HistoryStats HistoryStats();

        void ClearHistory();
    }
}
