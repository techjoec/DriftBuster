using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using DriftBuster.Gui.Services;
using DriftBuster.Gui.Views;

namespace DriftBuster.Gui.ViewModels
{
    public partial class MainWindowViewModel : ObservableObject
    {
        public enum MainViewSection
        {
            Diff,
            Hunt,
            Profiles,
            MultiServer,
        }

        private readonly IDriftbusterService _service;
        private readonly IToastService _toastService;
        private readonly Func<IDriftbusterService, object> _diffViewFactory;
        private readonly Func<IDriftbusterService, string?, object> _huntViewFactory;
        private readonly Func<IDriftbusterService, object> _profilesViewFactory;
        private readonly Func<IDriftbusterService, IToastService, PerformanceProfile, object> _serverSelectionFactory;
        private readonly PerformanceProfile _performanceProfile;
        private readonly IThemeRuntime _themeRuntime;

        [ObservableProperty]
        private object? _currentView;

        [ObservableProperty]
        private MainViewSection _activeView;

        public IAsyncRelayCommand PingCoreCommand { get; }
        public IAsyncRelayCommand CheckHealthCommand { get; }
        public IRelayCommand ShowDiffCommand { get; }
        public IRelayCommand ShowHuntCommand { get; }
        public IRelayCommand ShowProfilesCommand { get; }
        public IRelayCommand ShowMultiServerCommand { get; }

        public IToastService Toasts => _toastService;

        [ObservableProperty]
        private bool _isBackendHealthy;

        [ObservableProperty]
        private string _backendStatusText = "Checking…";

        public IReadOnlyList<ThemeOption> ThemeOptions { get; }

        [ObservableProperty]
        private ThemeOption? _selectedTheme;

        public MainWindowViewModel()
            : this(new DriftbusterService(), new ToastService())
        {
        }

        public MainWindowViewModel(
            IDriftbusterService service,
            IToastService toastService,
            Func<IDriftbusterService, object>? diffViewFactory = null,
            Func<IDriftbusterService, string?, object>? huntViewFactory = null,
            Func<IDriftbusterService, object>? profilesViewFactory = null,
            Func<IDriftbusterService, IToastService, PerformanceProfile, object>? serverSelectionFactory = null,
            PerformanceProfile? performanceProfile = null,
            IThemeRuntime? themeRuntime = null)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
            _toastService = toastService ?? throw new ArgumentNullException(nameof(toastService));
            _diffViewFactory = diffViewFactory ?? CreateDiffView;
            _huntViewFactory = huntViewFactory ?? CreateHuntView;
            _profilesViewFactory = profilesViewFactory ?? CreateProfilesView;
            _serverSelectionFactory = serverSelectionFactory ?? CreateServerSelectionView;
            _performanceProfile = performanceProfile ?? PerformanceProfile.FromEnvironment();
            _themeRuntime = themeRuntime ?? ApplicationThemeRuntime.Instance;

            PingCoreCommand = new AsyncRelayCommand(PingCoreAsync);
            CheckHealthCommand = new AsyncRelayCommand(CheckHealthAsync);
            ShowDiffCommand = new RelayCommand(ShowDiff);
            ShowHuntCommand = new RelayCommand(() => ShowHunt());
            ShowProfilesCommand = new RelayCommand(ShowProfiles);
            ShowMultiServerCommand = new RelayCommand(ShowMultiServer);
            ShowDiff();
            _ = CheckHealthCommand.ExecuteAsync(null);

            ThemeOptions = _themeRuntime.GetAvailableThemes();
            if (ThemeOptions.Count > 0)
            {
                SelectedTheme = _themeRuntime.GetDefaultTheme(ThemeOptions);
            }
        }

        public bool IsDiffSelected => ActiveView == MainViewSection.Diff;

        public bool IsHuntSelected => ActiveView == MainViewSection.Hunt;

        public bool IsProfilesSelected => ActiveView == MainViewSection.Profiles;

        public bool IsMultiServerSelected => ActiveView == MainViewSection.MultiServer;

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

        public void ShowMultiServer()
        {
            ActiveView = MainViewSection.MultiServer;
            CurrentView = _serverSelectionFactory(_service, _toastService, _performanceProfile);
        }

        public PerformanceProfile PerformanceProfile => _performanceProfile;

        private async Task PingCoreAsync()
        {
            try
            {
                var response = await _service.PingAsync();
                ShowHunt($"Ping reply: {response}");
                _toastService.Show("Ping succeeded", "Core responded successfully.", ToastLevel.Success, TimeSpan.FromSeconds(3));
            }
            catch (Exception ex)
            {
                ShowHunt($"Ping failed: {ex.Message}");
                _toastService.Show(
                    "Ping failed",
                    ex.Message,
                    ToastLevel.Error,
                    TimeSpan.FromSeconds(8),
                    new ToastAction("Copy details", () => CopyToClipboardAsync(ex.ToString())));
            }
        }

        private async Task CheckHealthAsync()
        {
            try
            {
                var response = await _service.PingAsync();
                IsBackendHealthy = true;
                BackendStatusText = $"Core OK: {response}";
                _toastService.Show("Core healthy", "Health check succeeded.", ToastLevel.Success, TimeSpan.FromSeconds(3));
            }
            catch (Exception ex)
            {
                IsBackendHealthy = false;
                BackendStatusText = $"Core unavailable: {ex.Message}";
                _toastService.Show(
                    "Core unavailable",
                    ex.Message,
                    ToastLevel.Error,
                    TimeSpan.FromSeconds(8),
                    new ToastAction("Copy details", () => CopyToClipboardAsync(ex.ToString())));
            }
        }

        private async Task CopyToClipboardAsync(string content)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                return;
            }

            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime)
            {
                var clipboard = lifetime.MainWindow?.Clipboard;
                if (clipboard is not null)
                {
                    await clipboard.SetTextAsync(content).ConfigureAwait(false);
                }
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

        private static object CreateServerSelectionView(IDriftbusterService service, IToastService toastService, PerformanceProfile performanceProfile) => new ServerSelectionView
        {
            DataContext = new ServerSelectionViewModel(service, toastService, performanceProfile: performanceProfile),
        };

        partial void OnActiveViewChanged(MainViewSection value)
        {
            OnPropertyChanged(nameof(IsDiffSelected));
            OnPropertyChanged(nameof(IsHuntSelected));
            OnPropertyChanged(nameof(IsProfilesSelected));
            OnPropertyChanged(nameof(IsMultiServerSelected));
        }

        partial void OnSelectedThemeChanged(ThemeOption? value)
        {
            if (value is null)
            {
                return;
            }

            _themeRuntime.ApplyTheme(value);
        }
    }
}
