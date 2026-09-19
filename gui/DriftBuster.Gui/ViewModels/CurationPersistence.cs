namespace DriftBuster.Gui.ViewModels
{
    /// <summary>How long a choice lasts: until the app closes, for every run, or whenever these servers are compared.</summary>
    public enum CurationPersistence
    {
        ThisRun,
        AllRuns,
        TheseServers,
    }
}
