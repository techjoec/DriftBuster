using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DriftBuster.Gui.Services;
using DriftBuster.Gui.Views;
using System;
using System.Threading.Tasks;

namespace DriftBuster.Gui.ViewModels
{
    public partial class MainWindowViewModel : ObservableObject
    {
        public enum MainViewSection
        {
            Diff,
            Hunt,
            Profiles,
        }

        private readonly IDriftbusterService _service;
        private readonly Func<IDriftbusterService, object> _diffViewFactory;
        private readonly Func<IDriftbusterService, string?, object> _huntViewFactory;
        private readonly Func<IDriftbusterService, object> _profilesViewFactory;

        [ObservableProperty]
        private object? _currentView;

        [ObservableProperty]
        private MainViewSection _activeView;

        public IAsyncRelayCommand PingCoreCommand { get; }
        public IAsyncRelayCommand CheckHealthCommand { get; }
        public IRelayCommand ShowDiffCommand { get; }
        public IRelayCommand ShowHuntCommand { get; }
        public IRelayCommand ShowProfilesCommand { get; }

        [ObservableProperty]
        private bool _isBackendHealthy;

        [ObservableProperty]
        private string _backendStatusText = "Checking…";

        public MainWindowViewModel()
            : this(new DriftbusterService())
        {
        }

        public MainWindowViewModel(
            IDriftbusterService service,
            Func<IDriftbusterService, object>? diffViewFactory = null,
            Func<IDriftbusterService, string?, object>? huntViewFactory = null,
            Func<IDriftbusterService, object>? profilesViewFactory = null)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
            _diffViewFactory = diffViewFactory ?? CreateDiffView;
            _huntViewFactory = huntViewFactory ?? CreateHuntView;
            _profilesViewFactory = profilesViewFactory ?? CreateProfilesView;

            PingCoreCommand = new AsyncRelayCommand(PingCoreAsync);
            CheckHealthCommand = new AsyncRelayCommand(CheckHealthAsync);
            ShowDiffCommand = new RelayCommand(ShowDiff);
            ShowHuntCommand = new RelayCommand(() => ShowHunt());
            ShowProfilesCommand = new RelayCommand(ShowProfiles);
            ShowDiff();
            _ = CheckHealthCommand.ExecuteAsync(null);
        }

        public bool IsDiffSelected => ActiveView == MainViewSection.Diff;

        public bool IsHuntSelected => ActiveView == MainViewSection.Hunt;

        public bool IsProfilesSelected => ActiveView == MainViewSection.Profiles;

        public void ShowDiff()
        {
            ActiveView = MainViewSection.Diff;
            CurrentView = _diffViewFactory(_service);
        }

        public void ShowHunt(string? initial = null)
        {
            ActiveView = MainViewSection.Hunt;
            CurrentView = _huntViewFactory(_service, initial);
        }

        public void ShowProfiles()
        {
            ActiveView = MainViewSection.Profiles;
            CurrentView = _profilesViewFactory(_service);
        }

        private async Task PingCoreAsync()
        {
            try
            {
                var response = await _service.PingAsync();
                ShowHunt($"Ping reply: {response}");
            }
            catch (Exception ex)
            {
                ShowHunt($"Ping failed: {ex.Message}");
            }
        }

        private async Task CheckHealthAsync()
        {
            try
            {
                var response = await _service.PingAsync();
                IsBackendHealthy = true;
                BackendStatusText = $"Core OK: {response}";
            }
            catch (Exception ex)
            {
                IsBackendHealthy = false;
                BackendStatusText = $"Core unavailable: {ex.Message}";
            }
        }

        private static object CreateDiffView(IDriftbusterService service) => new DiffView
        {
            DataContext = new DiffViewModel(service),
        };

        private static object CreateHuntView(IDriftbusterService service, string? initial) => new HuntView
        {
            DataContext = new HuntViewModel(service, initial),
        };

        private static object CreateProfilesView(IDriftbusterService service)
        {
            var viewModel = new RunProfilesViewModel(service);
            _ = viewModel.RefreshCommand.ExecuteAsync(null);
            return new RunProfilesView
            {
                DataContext = viewModel,
            };
        }

        partial void OnActiveViewChanged(MainViewSection value)
        {
            OnPropertyChanged(nameof(IsDiffSelected));
            OnPropertyChanged(nameof(IsHuntSelected));
            OnPropertyChanged(nameof(IsProfilesSelected));
        }
    }
}
