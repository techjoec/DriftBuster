namespace DriftBuster.Gui.ViewModels
{
    public sealed record ConfigDrilldownExportRequest(string DisplayName, string ConfigId, ConfigDrilldownViewModel.ExportFormat Format, string Payload);
}
