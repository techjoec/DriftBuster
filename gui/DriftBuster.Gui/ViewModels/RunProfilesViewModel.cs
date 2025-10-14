using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Globalization;
using System.IO;
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

    private static readonly char[] GlobCharacters = { '*', '?', '[' };

    public ObservableCollection<RunProfileDefinition> Profiles { get; } = new();
    public ObservableCollection<SourceEntry> Sources { get; } = new();
    public ObservableCollection<KeyValueEntry> Options { get; } = new();
    public ObservableCollection<RunResultEntry> RunResults { get; } = new();

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

    [ObservableProperty]
    private string? _outputDirectory;

    public IAsyncRelayCommand RefreshCommand { get; }
    public IAsyncRelayCommand SaveCommand { get; }
    public IAsyncRelayCommand RunCommand { get; }
    public IRelayCommand AddSourceCommand { get; }
    public IRelayCommand<SourceEntry> RemoveSourceCommand { get; }
    public IRelayCommand AddOptionCommand { get; }
    public IRelayCommand<KeyValueEntry> RemoveOptionCommand { get; }
    public IRelayCommand<RunProfileDefinition> LoadProfileCommand { get; }
    public IRelayCommand OpenOutputCommand { get; }

    public bool HasRunResults => RunResults.Count > 0;

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
        OpenOutputCommand = new RelayCommand(OpenOutput, () => !string.IsNullOrWhiteSpace(OutputDirectory) && Directory.Exists(OutputDirectory));

        RunResults.CollectionChanged += OnRunResultsChanged;

        if (Sources.Count == 0)
        {
            AddSourceEntry(string.Empty, isBaseline: true);
        }

        ValidateSources();
    }

    partial void OnProfileNameChanged(string value)
    {
        SaveCommand.NotifyCanExecuteChanged();
        RunCommand.NotifyCanExecuteChanged();
    }

    partial void OnOutputDirectoryChanged(string? value)
    {
        OpenOutputCommand.NotifyCanExecuteChanged();
    }

    private void AddSource()
    {
        AddSourceEntry(string.Empty, Sources.All(entry => !entry.IsBaseline));
        RemoveSourceCommand.NotifyCanExecuteChanged();
        ValidateSources();
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
        ValidateSources();
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
        if (string.IsNullOrWhiteSpace(ProfileName))
        {
            return false;
        }

        if (Sources.Count == 0)
        {
            return false;
        }

        var baseline = Sources.FirstOrDefault(source => source.IsBaseline);
        if (baseline is null || !string.IsNullOrWhiteSpace(baseline.Error))
        {
            return false;
        }

        var hasValid = false;

        foreach (var source in Sources)
        {
            if (!string.IsNullOrWhiteSpace(source.Error))
            {
                if (!string.IsNullOrWhiteSpace(source.Path))
                {
                    return false;
                }

                continue;
            }

            if (!string.IsNullOrWhiteSpace(source.Path))
            {
                hasValid = true;
            }
        }

        return hasValid;
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
        ClearRunResults();

        try
        {
            IsBusy = true;
            var profile = BuildCurrentProfile();
            var result = await _service.RunProfileAsync(profile, saveProfile: true).ConfigureAwait(false);
            PopulateRunResults(result);
            StatusMessage = result.Files.Length == 0
                ? "Run complete. No files were copied."
                : $"Run complete. Files copied: {result.Files.Length}.";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            ClearRunResults();
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

        ClearRunResults();

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
            Error = null,
        };
        Sources.Add(entry);
        entry.IsBaseline = false;
        var becameBaseline = false;
        if (isBaseline || Sources.All(source => !source.IsBaseline))
        {
            entry.IsBaseline = true;
            becameBaseline = true;
        }

        if (!becameBaseline)
        {
            ValidateSources();
        }
        return entry;
    }

    private void NotifyCommands()
    {
        SaveCommand.NotifyCanExecuteChanged();
        RunCommand.NotifyCanExecuteChanged();
    }

    private void PopulateRunResults(RunProfileRunResult result)
    {
        RunResults.Clear();
        foreach (var file in result.Files.OrderBy(file => file.Source, StringComparer.OrdinalIgnoreCase))
        {
            RunResults.Add(new RunResultEntry(file.Source, file.Destination, file.Size, file.Sha256));
        }

        OutputDirectory = string.IsNullOrWhiteSpace(result.OutputDir) ? null : result.OutputDir;
    }

    private void ClearRunResults()
    {
        RunResults.Clear();
        OutputDirectory = null;
    }

    private void OpenOutput()
    {
        if (string.IsNullOrWhiteSpace(OutputDirectory) || !Directory.Exists(OutputDirectory))
        {
            return;
        }

        try
        {
            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"\"{OutputDirectory}\"",
                    UseShellExecute = true,
                });
                return;
            }

            if (OperatingSystem.IsMacOS())
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "open",
                    Arguments = $"\"{OutputDirectory}\"",
                    UseShellExecute = false,
                });
                return;
            }

            if (OperatingSystem.IsLinux())
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "xdg-open",
                    Arguments = OutputDirectory,
                    UseShellExecute = false,
                });
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = OutputDirectory,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    private void ValidateSources()
    {
        foreach (var source in Sources)
        {
            source.Error = ValidateSourceEntry(source);
        }

        NotifyCommands();
    }

    private static string? ValidateSourceEntry(SourceEntry entry)
    {
        var path = entry.Path?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            return entry.IsBaseline ? "Select a baseline path." : "Select a source path.";
        }

        if (ContainsGlob(path))
        {
            var baseDirectory = TryGetGlobBaseDirectory(path);
            if (string.IsNullOrWhiteSpace(baseDirectory) || !Directory.Exists(baseDirectory))
            {
                return "Glob base directory not found.";
            }

            return null;
        }

        try
        {
            path = Path.GetFullPath(path);
        }
        catch (Exception)
        {
            return "Path is invalid.";
        }

        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return "Path does not exist.";
        }

        return null;
    }

    private static bool ContainsGlob(string value) => value.IndexOfAny(GlobCharacters) >= 0;

    private static string? TryGetGlobBaseDirectory(string value)
    {
        try
        {
            var normalized = value.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            var root = Path.GetPathRoot(normalized) ?? string.Empty;
            var remainder = normalized[root.Length..];

            var segments = remainder.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            var baseSegments = new List<string>();

            foreach (var segment in segments)
            {
                if (segment.IndexOfAny(GlobCharacters) >= 0)
                {
                    break;
                }

                baseSegments.Add(segment);
            }

            var baseDirectory = baseSegments.Count > 0
                ? Path.Combine(root, Path.Combine(baseSegments.ToArray()))
                : (string.IsNullOrEmpty(root) ? Environment.CurrentDirectory : root);

            return Path.GetFullPath(baseDirectory);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void OnRunResultsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasRunResults));
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
            Parent?.ValidateSources();
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

        ValidateSources();
    }

    public sealed class RunResultEntry
    {
        public RunResultEntry(string source, string destination, long size, string sha256)
        {
            Source = source;
            Destination = destination;
            Size = FormatSize(size);
            Hash = sha256;
        }

        public string Source { get; }

        public string Destination { get; }

        public string Size { get; }

        public string Hash { get; }

        private static string FormatSize(long size)
        {
            if (size == 1)
            {
                return "1 byte";
            }

            var formatted = size.ToString("N0", CultureInfo.InvariantCulture);
            return $"{formatted} bytes";
        }
    }
}
