using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using DriftBuster.Backend.Models;
using DriftBuster.Gui.Services;

namespace DriftBuster.Gui.ViewModels;

public partial class RunProfilesViewModel : ObservableObject
{
    private readonly IDriftbusterService _service;

    public ObservableCollection<RunProfileDefinition> Profiles { get; } = new();
    public ObservableCollection<SourceEntry> Sources { get; } = new();
    public ObservableCollection<KeyValueEntry> Options { get; } = new();

    [ObservableProperty]
    private string _profileName = string.Empty;

    [ObservableProperty]
    private string? _profileDescription;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private RunProfileDefinition? _selectedProfile;

    public IAsyncRelayCommand RefreshCommand { get; }
    public IAsyncRelayCommand SaveCommand { get; }
    public IAsyncRelayCommand RunCommand { get; }
    public IRelayCommand AddSourceCommand { get; }
    public IRelayCommand<SourceEntry> RemoveSourceCommand { get; }
    public IRelayCommand AddOptionCommand { get; }
    public IRelayCommand<KeyValueEntry> RemoveOptionCommand { get; }
    public IRelayCommand<RunProfileDefinition> LoadProfileCommand { get; }

    public RunProfilesViewModel(IDriftbusterService service)
    {
        _service = service;

        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        SaveCommand = new AsyncRelayCommand(SaveAsync, CanSave);
        RunCommand = new AsyncRelayCommand(RunAsync, CanRun);

        AddSourceCommand = new RelayCommand(AddSource);
        RemoveSourceCommand = new RelayCommand<SourceEntry>(RemoveSource, source => source is not null && Sources.Count > 1);
        AddOptionCommand = new RelayCommand(AddOption);
        RemoveOptionCommand = new RelayCommand<KeyValueEntry>(RemoveOption, option => option is not null);
        LoadProfileCommand = new RelayCommand<RunProfileDefinition>(LoadProfile, profile => profile is not null);

        if (Sources.Count == 0)
        {
            AddSourceEntry(string.Empty, isBaseline: true);
        }
    }

    partial void OnProfileNameChanged(string value)
    {
        SaveCommand.NotifyCanExecuteChanged();
        RunCommand.NotifyCanExecuteChanged();
    }

    private void AddSource()
    {
        AddSourceEntry(string.Empty, Sources.All(entry => !entry.IsBaseline));
        RemoveSourceCommand.NotifyCanExecuteChanged();
        NotifyCommands();
    }

    private void RemoveSource(SourceEntry? entry)
    {
        if (entry is null)
        {
            return;
        }

        Sources.Remove(entry);
        if (Sources.Count == 0)
        {
            AddSourceEntry(string.Empty, isBaseline: true);
        }
        else if (Sources.All(source => !source.IsBaseline))
        {
            Sources[0].IsBaseline = true;
        }

        RemoveSourceCommand.NotifyCanExecuteChanged();
        NotifyCommands();
    }

    private void AddOption()
    {
        Options.Add(new KeyValueEntry { Key = string.Empty, Value = string.Empty });
    }

    private void RemoveOption(KeyValueEntry? entry)
    {
        if (entry is null)
        {
            return;
        }

        Options.Remove(entry);
    }

    private async Task RefreshAsync()
    {
        try
        {
            IsBusy = true;
            var previous = SelectedProfile?.Name;
            Profiles.Clear();
            var response = await _service.ListProfilesAsync().ConfigureAwait(false);
            foreach (var profile in response.Profiles.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
            {
                Profiles.Add(profile);
            }
            StatusMessage = Profiles.Count == 0 ? "No saved profiles." : $"Loaded {Profiles.Count} profile(s).";
            if (!string.IsNullOrWhiteSpace(previous))
            {
                SelectedProfile = Profiles.FirstOrDefault(profile => string.Equals(profile.Name, previous, StringComparison.OrdinalIgnoreCase));
            }
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    partial void OnSelectedProfileChanged(RunProfileDefinition? value)
    {
        LoadProfileCommand.NotifyCanExecuteChanged();
    }

    private bool CanSave()
    {
        return !string.IsNullOrWhiteSpace(ProfileName) && Sources.Any(source => !string.IsNullOrWhiteSpace(source.Path));
    }

    private async Task SaveAsync()
    {
        try
        {
            IsBusy = true;
            var profile = BuildCurrentProfile();
            await _service.SaveProfileAsync(profile).ConfigureAwait(false);
            StatusMessage = $"Saved profile '{profile.Name}'.";
            await RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanRun()
    {
        return CanSave();
    }

    private async Task RunAsync()
    {
        try
        {
            IsBusy = true;
            var profile = BuildCurrentProfile();
            var result = await _service.RunProfileAsync(profile, saveProfile: true).ConfigureAwait(false);
            StatusMessage = $"Run complete. Files copied: {result.Files.Length}. Output: {result.OutputDir}";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private RunProfileDefinition BuildCurrentProfile()
    {
        var baseline = Sources.FirstOrDefault(entry => entry.IsBaseline && !string.IsNullOrWhiteSpace(entry.Path));
        var others = Sources
            .Where(entry => !ReferenceEquals(entry, baseline))
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Path))
            .Select(entry => entry.Path.Trim());

        var orderedSources = new System.Collections.Generic.List<string>();
        if (baseline is not null && !string.IsNullOrWhiteSpace(baseline.Path))
        {
            orderedSources.Add(baseline.Path.Trim());
        }
        orderedSources.AddRange(others);

        return new RunProfileDefinition
        {
            Name = ProfileName.Trim(),
            Description = string.IsNullOrWhiteSpace(ProfileDescription) ? null : ProfileDescription?.Trim(),
            Sources = orderedSources.ToArray(),
            Baseline = baseline?.Path?.Trim() ?? orderedSources.FirstOrDefault(),
            Options = Options
                .Where(option => !string.IsNullOrWhiteSpace(option.Key))
                .ToDictionary(option => option.Key.Trim(), option => option.Value ?? string.Empty),
        };
    }

    private void LoadProfile(RunProfileDefinition? profile)
    {
        if (profile is null)
        {
            return;
        }

        ProfileName = profile.Name;
        ProfileDescription = profile.Description;

        Sources.Clear();
        for (var index = 0; index < profile.Sources.Length; index++)
        {
            var path = profile.Sources[index];
            var isBaseline = profile.Baseline is not null
                ? string.Equals(path, profile.Baseline, StringComparison.OrdinalIgnoreCase)
                : index == 0;
            AddSourceEntry(path, isBaseline);
        }
        if (Sources.Count == 0)
        {
            AddSourceEntry(string.Empty, isBaseline: true);
        }

        Options.Clear();
        foreach (var option in profile.Options)
        {
            Options.Add(new KeyValueEntry { Key = option.Key, Value = option.Value });
        }

        SelectedProfile = profile;
        StatusMessage = $"Loaded profile '{profile.Name}'.";
        NotifyCommands();
    }

    private SourceEntry AddSourceEntry(string path, bool isBaseline)
    {
        var entry = new SourceEntry
        {
            Path = path,
            Parent = this,
            Error = string.Empty,
        };
        Sources.Add(entry);
        entry.IsBaseline = false;
        if (isBaseline || Sources.All(source => !source.IsBaseline))
        {
            entry.IsBaseline = true;
        }
        return entry;
    }

    private void NotifyCommands()
    {
        SaveCommand.NotifyCanExecuteChanged();
        RunCommand.NotifyCanExecuteChanged();
    }

    public sealed partial class SourceEntry : ObservableObject
    {
        [ObservableProperty]
        private string _path = string.Empty;

        [ObservableProperty]
        private bool _isBaseline;

        [ObservableProperty]
        private string? _error;

        partial void OnIsBaselineChanged(bool value)
        {
            Parent?.HandleBaselineChanged(this, value);
        }

        partial void OnPathChanged(string value)
        {
            Parent?.NotifyCommands();
        }

        public RunProfilesViewModel? Parent { get; set; }
    }

    public sealed partial class KeyValueEntry : ObservableObject
    {
        [ObservableProperty]
        private string _key = string.Empty;

        [ObservableProperty]
        private string? _value;
    }

    private bool _updatingBaseline;

    internal void HandleBaselineChanged(SourceEntry entry, bool isBaseline)
    {
        if (_updatingBaseline)
        {
            return;
        }

        if (isBaseline)
        {
            _updatingBaseline = true;
            foreach (var source in Sources)
            {
                source.IsBaseline = ReferenceEquals(source, entry);
            }
            _updatingBaseline = false;
        }
        else if (Sources.All(source => !source.IsBaseline))
        {
            _updatingBaseline = true;
            entry.IsBaseline = true;
            _updatingBaseline = false;
        }

        NotifyCommands();
    }
}
