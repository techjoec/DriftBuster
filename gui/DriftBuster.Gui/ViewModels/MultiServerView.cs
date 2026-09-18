namespace DriftBuster.Gui.ViewModels
{
    /// <summary>The Multi-server page's sub-views.</summary>
    public enum MultiServerView
    {
        /// <summary>Hosts, roots and the run controls.</summary>
        Setup,

        /// <summary>The settings comparison: what differs between servers.</summary>
        Compare,

        /// <summary>The file catalog (formats, coverage, drift counts).</summary>
        Details,

        /// <summary>One file's drilldown.</summary>
        Drilldown,
    }
}
