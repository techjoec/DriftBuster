namespace DriftBuster.Gui.ViewModels
{
    /// <summary>How a copy is written: JSON, tab-separated, plain text, or the plain text's UTF-8 bytes in hex.</summary>
    public enum CompareCopyFormat
    {
        Json,
        Tsv,
        Text,
        Hex,
    }
}
