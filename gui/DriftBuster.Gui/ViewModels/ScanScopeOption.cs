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
    }
}
