namespace DriftBuster.Gui.ViewModels
{
    /// <summary>What was right-clicked: a file, a setting in it, and optionally one server's value of that setting.</summary>
    public sealed record CompareContext(CompareFileViewModel File, CompareRowViewModel? Row = null, CompareCellViewModel? Cell = null)
    {
        public string Path => File.Path;

        public string? Key => Row?.Key;
    }
}
