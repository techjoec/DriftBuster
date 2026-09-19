using System.Collections.Generic;

using DriftBuster.Backend.History;

namespace DriftBuster.Gui.ViewModels
{
    /// <summary>
    /// History for one setting and value: the setting's recorded values in this file over time, where else the setting is set,
    /// and where else the value appears.
    /// </summary>
    public sealed record CompareHistory(
        IReadOnlyList<HistoryEntry> SettingOverTime,
        IReadOnlyList<HistoryEntry> SettingElsewhere,
        IReadOnlyList<HistoryEntry> ValueElsewhere)
    {
        public bool IsEmpty => SettingOverTime.Count == 0 && SettingElsewhere.Count == 0 && ValueElsewhere.Count == 0;
    }
}
