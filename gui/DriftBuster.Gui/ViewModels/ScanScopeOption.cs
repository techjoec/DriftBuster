using DriftBuster.Backend.Models;

namespace DriftBuster.Gui.ViewModels
{
    public sealed class ScanScopeOption
    {
        public ScanScopeOption(ServerScanScope value, string displayName)
        {
            Value = value;
            DisplayName = displayName;
        }

        public ServerScanScope Value { get; }

        public string DisplayName { get; }

        // UI Automation reads a combo box's selected value through ToString.
        public override string ToString() => DisplayName;
    }
}
