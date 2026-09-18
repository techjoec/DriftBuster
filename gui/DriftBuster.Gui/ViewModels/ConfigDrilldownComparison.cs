namespace DriftBuster.Gui.ViewModels
{
    /// <summary>A server whose copy of the file can be shown against the baseline's.</summary>
    public sealed class ConfigDrilldownComparison
    {
        public ConfigDrilldownComparison(string hostId, string label, string after, string unifiedDiff)
        {
            HostId = hostId;
            Label = label;
            After = after;
            UnifiedDiff = unifiedDiff;
        }

        public string HostId { get; }

        public string Label { get; }

        public string After { get; }

        public string UnifiedDiff { get; }

        public override string ToString() => Label;
    }
}
