using CommunityToolkit.Mvvm.ComponentModel;

namespace DriftBuster.Gui.ViewModels
{
    public sealed partial class RootEntryViewModel : ObservableObject
    {
        public RootEntryViewModel(string path)
        {
            _path = path.Trim();
        }

        [ObservableProperty]
        private string _path = string.Empty;

        [ObservableProperty]
        private RootValidationState _validationState = RootValidationState.Pending;

        [ObservableProperty]
        private string _statusMessage = string.Empty;
    }
}
