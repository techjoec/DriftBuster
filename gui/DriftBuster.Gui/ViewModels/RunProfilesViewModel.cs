using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
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
    private readonly ObservableCollection<string> _profileSuggestions = new();

    internal Func<ProcessStartInfo, Process?>? ProcessStarterOverride { get; set; }

    private static readonly char[] GlobCharacters = { '*', '?', '[' };

    public ObservableCollection<RunProfileDefinition> Profiles { get; } = new();
    public ObservableCollection<SourceEntry> Sources { get; } = new();
    public ObservableCollection<KeyValueEntry> Options { get; } = new();
    public ObservableCollection<RunResultEntry> RunResults { get; } = new();
    public ObservableCollection<ScheduleEntry> Schedules { get; } = new();
    public ReadOnlyObservableCollection<string> ProfileSuggestions { get; }

    [ObservableProperty]
    private SecretScannerOptions _secretScanner = new();

    [ObservableProperty]
    private string _profileName = string.Empty;

    private string _previousProfileName = string.Empty;

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
    public IRelayCommand AddScheduleCommand { get; }
    public IRelayCommand<ScheduleEntry> RemoveScheduleCommand { get; }
    public IRelayCommand<RunProfileDefinition> LoadProfileCommand { get; }
    public IRelayCommand OpenOutputCommand { get; }

    public bool HasRunResults => RunResults.Count > 0;

    public string SecretScannerSummary
    {
        get
        {
            var ruleCount = SecretScanner.IgnoreRules?.Length ?? 0;
            var patternCount = SecretScanner.IgnorePatterns?.Length ?? 0;
            if (ruleCount == 0 && patternCount == 0)
            {
                return "Secret scanner active. No ignores configured.";
            }

            return $"Secret scanner active. Ignored rules: {ruleCount}, patterns: {patternCount}.";
        }
    }

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
        AddScheduleCommand = new RelayCommand(AddSchedule);
        RemoveScheduleCommand = new RelayCommand<ScheduleEntry>(RemoveSchedule, schedule => schedule is not null);
        LoadProfileCommand = new RelayCommand<RunProfileDefinition>(LoadProfile, profile => profile is not null);
        OpenOutputCommand = new RelayCommand(OpenOutput, () => !string.IsNullOrWhiteSpace(OutputDirectory) && Directory.Exists(OutputDirectory));

        RunResults.CollectionChanged += OnRunResultsChanged;
        Schedules.CollectionChanged += OnSchedulesCollectionChanged;
        Profiles.CollectionChanged += OnProfilesCollectionChanged;

        ProfileSuggestions = new ReadOnlyObservableCollection<string>(_profileSuggestions);

        if (Sources.Count == 0)
        {
            AddSourceEntry(string.Empty, isBaseline: true);
        }

        ValidateSources();
        RebuildProfileSuggestions();
    }

    partial void OnSecretScannerChanged(SecretScannerOptions value)
    {
        OnPropertyChanged(nameof(SecretScannerSummary));
    }

    partial void OnProfileNameChanged(string value)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        UpdateScheduleProfileDefaults(trimmed);
        _previousProfileName = trimmed;
        RebuildProfileSuggestions();
        NotifyCommands();
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

    private void AddSchedule()
    {
        var profile = string.IsNullOrWhiteSpace(ProfileName)
            ? SelectedProfile?.Name ?? string.Empty
            : ProfileName.Trim();
        var entry = new ScheduleEntry(this)
        {
            Profile = profile,
        };
        Schedules.Add(entry);
    }

    private void RemoveSchedule(ScheduleEntry? entry)
    {
        if (entry is null)
        {
            return;
        }

        entry.Detach();
        Schedules.Remove(entry);
    }

    private void OnSchedulesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            foreach (var schedule in Schedules)
            {
                schedule.PropertyChanged -= OnScheduleEntryPropertyChanged;
                schedule.PropertyChanged += OnScheduleEntryPropertyChanged;
            }
        }
        else
        {
            if (e.OldItems is not null)
            {
                foreach (ScheduleEntry schedule in e.OldItems)
                {
                    schedule.PropertyChanged -= OnScheduleEntryPropertyChanged;
                    schedule.Detach();
                }
            }

            if (e.NewItems is not null)
            {
                foreach (ScheduleEntry schedule in e.NewItems)
                {
                    schedule.PropertyChanged += OnScheduleEntryPropertyChanged;
                }
            }
        }

        RemoveScheduleCommand.NotifyCanExecuteChanged();
        ValidateSchedules();
    }

    private void OnScheduleEntryPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        ValidateSchedules();
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
            var scheduleResponse = await _service.ListSchedulesAsync().ConfigureAwait(false);
            ApplySchedules(scheduleResponse.Schedules ?? Array.Empty<ScheduleDefinition>());
            RebuildProfileSuggestions();
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

        if (!hasValid)
        {
            return false;
        }

        foreach (var schedule in Schedules)
        {
            if (schedule.IsBlank)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(schedule.Error))
            {
                return false;
            }
        }

        return true;
    }

    private async Task SaveAsync()
    {
        try
        {
            IsBusy = true;
            var profile = BuildCurrentProfile();
            var schedules = BuildCurrentSchedules();
            await _service.SaveProfileAsync(profile).ConfigureAwait(false);
            await _service.SaveSchedulesAsync(schedules).ConfigureAwait(false);
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
            var schedules = BuildCurrentSchedules();
            await _service.SaveSchedulesAsync(schedules).ConfigureAwait(false);
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

    public async Task PrepareOfflineCollectorAsync(string packagePath)
    {
        if (IsBusy)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(packagePath))
        {
            StatusMessage = "Select an output path for the offline collector.";
            return;
        }

        if (!CanSave())
        {
            StatusMessage = "Configure a valid profile before preparing an offline collector.";
            return;
        }

        var profile = BuildCurrentProfile();

        try
        {
            IsBusy = true;
            var schedules = BuildCurrentSchedules();
            await _service.SaveProfileAsync(profile).ConfigureAwait(false);
            await _service.SaveSchedulesAsync(schedules).ConfigureAwait(false);

            var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["prepared_at"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                ["profile_name"] = profile.Name,
            };

            var user = Environment.UserName;
            if (!string.IsNullOrWhiteSpace(user))
            {
                metadata["prepared_by"] = user;
            }

            var request = new OfflineCollectorRequest
            {
                PackagePath = packagePath,
                Metadata = metadata,
            };

            var result = await _service.PrepareOfflineCollectorAsync(profile, request).ConfigureAwait(false);
            StatusMessage = $"Offline collector saved to '{result.PackagePath}'.";
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
            SecretScanner = CloneSecretScannerOptions(SecretScanner),
        };
    }

    private ScheduleDefinition[] BuildCurrentSchedules()
    {
        return Schedules
            .Where(schedule => !schedule.IsBlank)
            .Select(schedule => schedule.ToDefinition())
            .ToArray();
    }

    public void ApplySecretScanner(SecretScannerOptions? options)
    {
        SecretScanner = CloneSecretScannerOptions(options);
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

        ApplySecretScanner(profile.SecretScanner);

        SelectedProfile = profile;
        StatusMessage = $"Loaded profile '{profile.Name}'.";
        NotifyCommands();
    }

    private void ApplySchedules(IEnumerable<ScheduleDefinition> schedules)
    {
        foreach (var existing in Schedules.ToArray())
        {
            existing.PropertyChanged -= OnScheduleEntryPropertyChanged;
            existing.Detach();
        }

        Schedules.Clear();

        if (schedules is null)
        {
            ValidateSchedules();
            return;
        }

        foreach (var definition in schedules.OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase))
        {
            var entry = new ScheduleEntry(this)
            {
                Name = definition.Name ?? string.Empty,
                Profile = definition.Profile ?? string.Empty,
                Every = definition.Every ?? string.Empty,
                StartAt = string.IsNullOrWhiteSpace(definition.StartAt) ? null : definition.StartAt?.Trim(),
                WindowStart = definition.Window?.Start?.Trim(),
                WindowEnd = definition.Window?.End?.Trim(),
                WindowTimezone = definition.Window?.Timezone?.Trim(),
                TagsText = definition.Tags is { Length: > 0 }
                    ? string.Join(", ", definition.Tags)
                    : string.Empty,
            };

            if (definition.Metadata is not null)
            {
                foreach (var pair in definition.Metadata)
                {
                    entry.Metadata.Add(new KeyValueEntry { Key = pair.Key, Value = pair.Value });
                }
            }

            Schedules.Add(entry);
        }

        ValidateSchedules();
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

    private static SecretScannerOptions CloneSecretScannerOptions(SecretScannerOptions? options)
    {
        var clone = new SecretScannerOptions();
        if (options?.IgnoreRules is not null)
        {
            clone.IgnoreRules = options.IgnoreRules
                .Where(rule => !string.IsNullOrWhiteSpace(rule))
                .Select(rule => rule.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }

        if (options?.IgnorePatterns is not null)
        {
            clone.IgnorePatterns = options.IgnorePatterns
                .Where(pattern => !string.IsNullOrWhiteSpace(pattern))
                .Select(pattern => pattern.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }

        return clone;
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
                StartProcess(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"\"{OutputDirectory}\"",
                    UseShellExecute = true,
                });
                return;
            }

            if (OperatingSystem.IsMacOS())
            {
                StartProcess(new ProcessStartInfo
                {
                    FileName = "open",
                    Arguments = $"\"{OutputDirectory}\"",
                    UseShellExecute = false,
                });
                return;
            }

            if (OperatingSystem.IsLinux())
            {
                StartProcess(new ProcessStartInfo
                {
                    FileName = "xdg-open",
                    Arguments = OutputDirectory,
                    UseShellExecute = false,
                });
                return;
            }

            StartProcess(new ProcessStartInfo
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

    private Process? StartProcess(ProcessStartInfo startInfo)
    {
        if (ProcessStarterOverride is not null)
        {
            return ProcessStarterOverride(startInfo);
        }

        return Process.Start(startInfo);
    }

    private void ValidateSources()
    {
        foreach (var source in Sources)
        {
            source.Error = ValidateSourceEntry(source);
        }

        NotifyCommands();
    }

    private void ValidateSchedules()
    {
        foreach (var schedule in Schedules)
        {
            schedule.Error = ValidateScheduleEntry(schedule);
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

    private static string? ValidateScheduleEntry(ScheduleEntry entry)
    {
        if (entry.IsBlank)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(entry.Name))
        {
            return "Schedule name is required.";
        }

        if (string.IsNullOrWhiteSpace(entry.Profile))
        {
            return "Schedule profile is required.";
        }

        if (string.IsNullOrWhiteSpace(entry.Every))
        {
            return "Schedule interval is required.";
        }

        var hasWindowStart = !string.IsNullOrWhiteSpace(entry.WindowStart);
        var hasWindowEnd = !string.IsNullOrWhiteSpace(entry.WindowEnd);
        var hasWindowTimezone = !string.IsNullOrWhiteSpace(entry.WindowTimezone);

        if (hasWindowStart || hasWindowEnd || hasWindowTimezone)
        {
            if (!hasWindowStart || !hasWindowEnd)
            {
                return "Specify both window start and end times.";
            }

            if (!hasWindowTimezone)
            {
                return "Specify a timezone when defining a window.";
            }
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

    private void OnProfilesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RebuildProfileSuggestions();
    }

    private void UpdateScheduleProfileDefaults(string trimmedProfileName)
    {
        foreach (var schedule in Schedules)
        {
            if (!string.IsNullOrWhiteSpace(schedule.Profile))
            {
                if (!string.IsNullOrWhiteSpace(_previousProfileName) &&
                    string.Equals(schedule.Profile, _previousProfileName, StringComparison.OrdinalIgnoreCase))
                {
                    schedule.Profile = trimmedProfileName;
                }

                continue;
            }

            if (schedule.IsBlank || string.IsNullOrWhiteSpace(schedule.Profile))
            {
                schedule.Profile = trimmedProfileName;
            }
        }
    }

    private void RebuildProfileSuggestions()
    {
        var draftName = string.IsNullOrWhiteSpace(ProfileName) ? null : ProfileName.Trim();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        _profileSuggestions.Clear();

        if (!string.IsNullOrWhiteSpace(draftName) && seen.Add(draftName))
        {
            _profileSuggestions.Add(draftName);
        }

        foreach (var name in Profiles
                     .Select(profile => profile?.Name)
                     .Where(name => !string.IsNullOrWhiteSpace(name))
                     .Select(name => name!.Trim())
                     .OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
        {
            if (seen.Add(name))
            {
                _profileSuggestions.Add(name);
            }
        }
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

    public sealed partial class ScheduleEntry : ObservableObject
    {
        private static readonly char[] TagSeparators = { ',', ';', '\n' };

        public ScheduleEntry(RunProfilesViewModel parent)
        {
            Parent = parent;
            Metadata = new ObservableCollection<KeyValueEntry>();
            AddMetadataCommand = new RelayCommand(AddMetadata);
            RemoveMetadataCommand = new RelayCommand<KeyValueEntry>(RemoveMetadata, entry => entry is not null);
            Metadata.CollectionChanged += OnMetadataCollectionChanged;
        }

        public RunProfilesViewModel Parent { get; }

        public ObservableCollection<KeyValueEntry> Metadata { get; }

        public IRelayCommand AddMetadataCommand { get; }

        public IRelayCommand<KeyValueEntry> RemoveMetadataCommand { get; }

        [ObservableProperty]
        private string _name = string.Empty;

        [ObservableProperty]
        private string _profile = string.Empty;

        [ObservableProperty]
        private string _every = string.Empty;

        [ObservableProperty]
        private string? _startAt;

        [ObservableProperty]
        private string? _windowStart;

        [ObservableProperty]
        private string? _windowEnd;

        [ObservableProperty]
        private string? _windowTimezone;

        [ObservableProperty]
        private string _tagsText = string.Empty;

        [ObservableProperty]
        private string? _error;

        public bool IsBlank =>
            string.IsNullOrWhiteSpace(Name) &&
            string.IsNullOrWhiteSpace(Profile) &&
            string.IsNullOrWhiteSpace(Every) &&
            string.IsNullOrWhiteSpace(StartAt) &&
            string.IsNullOrWhiteSpace(WindowStart) &&
            string.IsNullOrWhiteSpace(WindowEnd) &&
            string.IsNullOrWhiteSpace(WindowTimezone) &&
            string.IsNullOrWhiteSpace(TagsText) &&
            Metadata.All(IsMetadataBlank);

        partial void OnNameChanged(string value) => Parent.ValidateSchedules();

        partial void OnProfileChanged(string value) => Parent.ValidateSchedules();

        partial void OnEveryChanged(string value) => Parent.ValidateSchedules();

        partial void OnStartAtChanged(string? value) => Parent.ValidateSchedules();

        partial void OnWindowStartChanged(string? value) => Parent.ValidateSchedules();

        partial void OnWindowEndChanged(string? value) => Parent.ValidateSchedules();

        partial void OnWindowTimezoneChanged(string? value) => Parent.ValidateSchedules();

        partial void OnTagsTextChanged(string value) => Parent.ValidateSchedules();

        internal ScheduleDefinition ToDefinition()
        {
            var definition = new ScheduleDefinition
            {
                Name = Name.Trim(),
                Profile = Profile.Trim(),
                Every = Every.Trim(),
                StartAt = string.IsNullOrWhiteSpace(StartAt) ? null : StartAt.Trim(),
            };

            var window = BuildWindow();
            if (window is not null)
            {
                definition.Window = window;
            }

            var tags = BuildTags();
            if (tags.Length > 0)
            {
                definition.Tags = tags;
            }

            var metadata = BuildMetadata();
            definition.Metadata = metadata.Count > 0
                ? metadata
                : new Dictionary<string, string>(System.StringComparer.Ordinal);

            return definition;
        }

        internal void Detach()
        {
            Metadata.CollectionChanged -= OnMetadataCollectionChanged;
            foreach (var entry in Metadata)
            {
                entry.PropertyChanged -= OnMetadataEntryPropertyChanged;
            }
        }

        private void AddMetadata()
        {
            Metadata.Add(new KeyValueEntry { Key = string.Empty, Value = string.Empty });
            Parent.ValidateSchedules();
        }

        private void RemoveMetadata(KeyValueEntry? entry)
        {
            if (entry is null)
            {
                return;
            }

            entry.PropertyChanged -= OnMetadataEntryPropertyChanged;
            Metadata.Remove(entry);
            Parent.ValidateSchedules();
        }

        private void OnMetadataCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.NewItems is not null)
            {
                foreach (KeyValueEntry entry in e.NewItems)
                {
                    entry.PropertyChanged += OnMetadataEntryPropertyChanged;
                }
            }

            if (e.OldItems is not null)
            {
                foreach (KeyValueEntry entry in e.OldItems)
                {
                    entry.PropertyChanged -= OnMetadataEntryPropertyChanged;
                }
            }

            Parent.ValidateSchedules();
        }

        private void OnMetadataEntryPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            Parent.ValidateSchedules();
        }

        private static bool IsMetadataBlank(KeyValueEntry entry)
        {
            return string.IsNullOrWhiteSpace(entry.Key) && string.IsNullOrWhiteSpace(entry.Value);
        }

        private ScheduleWindowDefinition? BuildWindow()
        {
            var start = string.IsNullOrWhiteSpace(WindowStart) ? null : WindowStart.Trim();
            var end = string.IsNullOrWhiteSpace(WindowEnd) ? null : WindowEnd.Trim();
            var timezone = string.IsNullOrWhiteSpace(WindowTimezone) ? null : WindowTimezone.Trim();
            if (start is null && end is null && timezone is null)
            {
                return null;
            }

            return new ScheduleWindowDefinition
            {
                Start = start,
                End = end,
                Timezone = timezone,
            };
        }

        private string[] BuildTags()
        {
            if (string.IsNullOrWhiteSpace(TagsText))
            {
                return System.Array.Empty<string>();
            }

            return TagsText
                .Split(TagSeparators, StringSplitOptions.RemoveEmptyEntries)
                .Select(tag => tag.Trim())
                .Where(tag => !string.IsNullOrWhiteSpace(tag))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private Dictionary<string, string> BuildMetadata()
        {
            var metadata = new Dictionary<string, string>(System.StringComparer.Ordinal);
            foreach (var entry in Metadata)
            {
                if (string.IsNullOrWhiteSpace(entry.Key))
                {
                    continue;
                }

                var key = entry.Key.Trim();
                if (metadata.ContainsKey(key))
                {
                    continue;
                }

                metadata[key] = entry.Value?.Trim() ?? string.Empty;
            }

            return metadata;
        }
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
