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

using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Models;
using DriftBuster.Backend.Profiles.Run;
using DriftBuster.Backend.Scheduling;
using DriftBuster.Gui.Services;

namespace DriftBuster.Gui.ViewModels;

public partial class RunProfilesViewModel : ObservableObject, IDisposable
{
    private readonly IDriftbusterService _service;
    private readonly ObservableCollection<string> _profileSuggestions = new();
    private bool _disposed;

    // True only while the schedule cards are the manifest as last read. Until then (before the first load, or after a load that failed:
    // a manifest the scheduler refuses) saving leaves schedules.json alone, so the cards never overwrite a manifest they were not read from.
    private bool _schedulesLoaded;

    private const string SchedulesNotSavedMessage = "Schedule cards were not saved: the schedule manifest has not been loaded.";

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

    // The last loaded profile's name, description and secret scanner options, and whether it had no baseline: an unedited load saves them
    // as loaded, so the saved profile behaves as the loaded one.
    private string? _loadedProfileName;
    private string? _loadedDescription;
    private SecretScannerOptions? _loadedSecretScanner;
    private bool _loadedWithoutBaseline;

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
            _schedulesLoaded = false;
            var previous = SelectedProfile?.Name;
            Profiles.Clear();
            var response = await _service.ListProfilesAsync().ConfigureAwait(true);
            foreach (var profile in response.Profiles.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
            {
                Profiles.Add(profile);
            }
            var scheduleError = await LoadSchedulesAsync().ConfigureAwait(true);
            RebuildProfileSuggestions();
            StatusMessage = scheduleError is not null
                ? $"The schedule manifest could not be loaded, so schedules are not saved until it loads: {scheduleError}"
                : Profiles.Count == 0 ? "No saved profiles." : $"Loaded {Profiles.Count} profile(s).";
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

    // Reads the manifest into the cards; on failure the cards are cleared and the error message returned.
    private async Task<string?> LoadSchedulesAsync()
    {
        try
        {
            var scheduleResponse = await _service.ListSchedulesAsync().ConfigureAwait(true);
            ApplySchedules(scheduleResponse.Schedules ?? Array.Empty<ScheduleDefinition>());
            _schedulesLoaded = true;
            return null;
        }
        catch (Exception ex)
        {
            ApplySchedules(Array.Empty<ScheduleDefinition>());
            return ex.Message;
        }
    }

    /// <summary>
    /// Writes the cards to the manifest when they were read from it; returns true when cards were left unsaved because the manifest has not
    /// loaded.
    /// </summary>
    private async Task<bool> SaveSchedulesIfLoadedAsync(ScheduleDefinition[] schedules)
    {
        if (!_schedulesLoaded)
        {
            return schedules.Length > 0;
        }

        await _service.SaveSchedulesAsync(schedules).ConfigureAwait(true);
        return false;
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
            await _service.SaveProfileAsync(profile).ConfigureAwait(true);
            var schedulesSkipped = await SaveSchedulesIfLoadedAsync(schedules).ConfigureAwait(true);
            await RefreshAsync().ConfigureAwait(true);
            if (schedulesSkipped && _schedulesLoaded)
            {
                StatusMessage = $"Saved profile '{profile.Name}'. {SchedulesNotSavedMessage}";
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
            var schedulesSkipped = await SaveSchedulesIfLoadedAsync(schedules).ConfigureAwait(true);
            var result = await _service.RunProfileAsync(profile, saveProfile: true).ConfigureAwait(true);
            ListSavedProfile(profile);
            PopulateRunResults(result);
            StatusMessage = (result.Files.Length == 0
                ? "Run complete. No files were copied."
                : $"Run complete. Files copied: {result.Files.Length}.") + (schedulesSkipped ? " " + SchedulesNotSavedMessage : string.Empty);
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
            await _service.SaveProfileAsync(profile).ConfigureAwait(true);
            var schedulesSkipped = await SaveSchedulesIfLoadedAsync(schedules).ConfigureAwait(true);

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

            var result = await _service.PrepareOfflineCollectorAsync(profile, request).ConfigureAwait(true);
            StatusMessage = $"Offline collector saved to '{result.PackagePath}'." + (schedulesSkipped ? " " + SchedulesNotSavedMessage : string.Empty);
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

    // The profile as edited. Sources keep their declared order (a structured profile collects in that order and names an aliasless source
    // by its position). The baseline is the saved spelling of its source, or none when the loaded profile had none and the baseline is
    // still the first source (the run then reads the first source as the baseline, as it did). A name, description, option key or secret
    // scanner list that is unedited since the load saves as loaded; an edited name or option key is stripped as Python's str.strip strips
    // it, and option keys are compared exactly (a later row with the same key wins).
    private RunProfileDefinition BuildCurrentProfile()
    {
        var sources = Sources.Where(entry => !string.IsNullOrWhiteSpace(entry.Path)).ToList();
        var baseline = sources.FirstOrDefault(entry => entry.IsBaseline);
        var keepNoBaseline = _loadedWithoutBaseline && baseline is not null && ReferenceEquals(baseline, sources[0]);

        return new RunProfileDefinition
        {
            Name = _loadedProfileName is not null && string.Equals(ProfileName, _loadedProfileName, StringComparison.Ordinal)
                ? _loadedProfileName
                : EngineText.Strip(ProfileName),
            Description = string.Equals(ProfileDescription, _loadedDescription, StringComparison.Ordinal)
                ? _loadedDescription
                : string.IsNullOrWhiteSpace(ProfileDescription) ? null : ProfileDescription.Trim(),
            Sources = sources.Select(entry => entry.ToSource()).ToArray(),
            Baseline = keepNoBaseline ? null : baseline?.ToSource().Path,
            Options = BuildOptions(),
            SecretScanner = ReferenceEquals(SecretScanner, _loadedSecretScanner) ? CopySecretScannerOptions(SecretScanner) : CloneSecretScannerOptions(SecretScanner),
        };
    }

    private Dictionary<string, string> BuildOptions()
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var option in Options)
        {
            var unedited = option.LoadedKey is not null && string.Equals(option.Key, option.LoadedKey, StringComparison.Ordinal);
            var key = unedited ? option.LoadedKey! : EngineText.Strip(option.Key ?? string.Empty);
            if (!unedited && key.Length == 0)
            {
                continue;
            }

            options[key] = option.Value ?? string.Empty;
        }

        return options;
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
        _loadedProfileName = profile.Name;
        _loadedDescription = profile.Description;
        _loadedWithoutBaseline = profile.Baseline is null;

        Sources.Clear();
        // The baseline is the first source whose path is exactly the profile's baseline (the store compares paths exactly).
        var baselineIndex = profile.Baseline is null ? 0 : Array.FindIndex(profile.Sources, source => string.Equals(source.Path, profile.Baseline, StringComparison.Ordinal));
        for (var index = 0; index < profile.Sources.Length; index++)
        {
            var source = profile.Sources[index];
            var isBaseline = index == baselineIndex;
            var entry = AddSourceEntry(source.Path, isBaseline);
            entry.Load(source);
        }
        if (Sources.Count == 0)
        {
            AddSourceEntry(string.Empty, isBaseline: true);
        }

        Options.Clear();
        foreach (var option in profile.Options)
        {
            Options.Add(new KeyValueEntry { Key = option.Key, Value = option.Value, LoadedKey = option.Key });
        }

        SecretScanner = CopySecretScannerOptions(profile.SecretScanner);
        _loadedSecretScanner = SecretScanner;

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

        // Manifest order: the scheduler registers schedules in that order, which decides the order of runs due together.
        foreach (var definition in schedules)
        {
            var entry = new ScheduleEntry(this)
            {
                Name = definition.Name ?? string.Empty,
                Profile = definition.Profile ?? string.Empty,
                Every = definition.Every ?? string.Empty,
                EveryValue = definition.EveryValue,
                MetadataValues = definition.MetadataValues,
                ManifestEntry = definition.ManifestEntry,
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

    // The lists exactly as they are (a loaded profile's patterns are regular expressions, where white space and repeats matter).
    private static SecretScannerOptions CopySecretScannerOptions(SecretScannerOptions? options) => new()
    {
        IgnoreRules = options?.IgnoreRules?.ToArray() ?? Array.Empty<string>(),
        IgnorePatterns = options?.IgnorePatterns?.ToArray() ?? Array.Empty<string>(),
    };

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

    // A run saves the profile; list it without reloading the schedule cards the way Refresh does.
    private void ListSavedProfile(RunProfileDefinition profile)
    {
        if (Profiles.Any(existing => string.Equals(existing.Name, profile.Name, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var index = Profiles.TakeWhile(existing => StringComparer.OrdinalIgnoreCase.Compare(existing.Name, profile.Name) < 0).Count();
        Profiles.Insert(index, profile);
        RebuildProfileSuggestions();
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
            source.Error = ValidateSourceEntry(source) ?? ValidateAlias(source);
        }

        NotifyCommands();
    }

    // Two sources whose aliases name the same run directory would merge or overwrite each other's files.
    private string? ValidateAlias(SourceEntry entry)
    {
        var directory = entry.AliasDirectory;
        if (directory is null)
        {
            return null;
        }

        var shared = Sources.Any(other => !ReferenceEquals(other, entry)
            && !string.IsNullOrWhiteSpace(other.Path)
            && string.Equals(other.AliasDirectory, directory, StringComparison.OrdinalIgnoreCase));
        return shared ? "Another source uses the same alias." : null;
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
        // The path as it will be saved and run.
        var path = entry.ToSource().Path;
        if (string.IsNullOrWhiteSpace(path))
        {
            return entry.IsBaseline ? "Select a baseline path." : "Select a source path.";
        }

        if (ContainsGlob(path))
        {
            var baseDirectory = TryGetGlobBaseDirectory(path);
            if (!entry.Optional && (string.IsNullOrWhiteSpace(baseDirectory) || !Directory.Exists(baseDirectory)))
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

        // An optional source that does not exist is skipped by the run.
        if (!entry.Optional && !File.Exists(path) && !Directory.Exists(path))
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

        // A window without a time zone is a UTC window, as ScheduleWindow.from_dict reads it.
        if ((hasWindowStart || hasWindowEnd || hasWindowTimezone) && (!hasWindowStart || !hasWindowEnd))
        {
            return "Specify both window start and end times.";
        }

        return ScheduleStore.ValidationError(entry.ToDefinition());
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
        private static readonly string[] LineBreaks = { "\r\n", "\n" };

        // The patterns the entry was loaded with and their text: saved unchanged while the text is, so a pattern holding a line break,
        // an empty pattern or any other text the editor cannot spell survives a load and save.
        private string[]? _loadedExclude;
        private string? _loadedExcludeText;

        // The path and alias the entry was loaded with: saved exactly as loaded while unedited, so a loaded alias with surrounding
        // whitespace keeps naming the same run directory.
        private string? _loadedPath;
        private string? _loadedAlias;

        [ObservableProperty]
        private string _path = string.Empty;

        /// <summary>The directory name the source's files are copied under; blank for the default.</summary>
        [ObservableProperty]
        private string? _alias;

        /// <summary>Whether a source that is missing or matches nothing is skipped instead of failing the run.</summary>
        [ObservableProperty]
        private bool _optional;

        /// <summary>Exclude patterns, one per line, each kept exactly as written (empty lines are ignored).</summary>
        [ObservableProperty]
        private string _exclude = string.Empty;

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

        partial void OnAliasChanged(string? value)
        {
            Parent?.ValidateSources();
        }

        partial void OnOptionalChanged(bool value)
        {
            Parent?.ValidateSources();
        }

        public RunProfilesViewModel? Parent { get; set; }

        /// <summary>The run directory the alias names (<c>_safe_name</c>), or null when the source has no alias.</summary>
        internal string? AliasDirectory => ToSource().Alias is { } alias ? RunProfileStore.SafeName(alias) : null;

        /// <summary>Shows a loaded source's alias, optional flag and exclude patterns, and remembers its path and alias as loaded.</summary>
        internal void Load(RunProfileSource source)
        {
            Alias = source.Alias;
            Optional = source.Optional;
            LoadExclude(source.Exclude ?? Array.Empty<string>());
            _loadedPath = Path;
            _loadedAlias = Alias;
        }

        /// <summary>Shows <paramref name="patterns"/> one per line and remembers them, so an unchanged editor saves them as loaded.</summary>
        internal void LoadExclude(IReadOnlyList<string> patterns)
        {
            _loadedExclude = patterns.ToArray();
            _loadedExcludeText = string.Join('\n', _loadedExclude);
            Exclude = _loadedExcludeText;
        }

        internal RunProfileSource ToSource() => new(SavedPath())
        {
            Alias = SavedAlias(),
            Optional = Optional,
            Exclude = ExcludePatterns(),
        };

        // The loaded path while unedited; an edited path stripped as Python's str.strip strips it.
        private string SavedPath()
            => _loadedPath is not null && string.Equals(Path, _loadedPath, StringComparison.Ordinal) ? _loadedPath : EngineText.Strip(Path ?? string.Empty);

        // The loaded alias while unedited; an edited alias stripped as Python's str.strip strips it, none when that leaves it empty (the
        // backend drops an alias str.strip empties).
        private string? SavedAlias()
        {
            if (_loadedAlias is not null && string.Equals(Alias, _loadedAlias, StringComparison.Ordinal))
            {
                return _loadedAlias;
            }

            var alias = EngineText.Strip(Alias ?? string.Empty);
            return alias.Length == 0 ? null : alias;
        }

        private string[] ExcludePatterns()
        {
            var text = Exclude ?? string.Empty;
            if (_loadedExclude is not null && string.Equals(text, _loadedExcludeText, StringComparison.Ordinal))
            {
                return _loadedExclude.ToArray();
            }

            return text.Split(LineBreaks, StringSplitOptions.None).Where(pattern => pattern.Length > 0).ToArray();
        }
    }

    public sealed partial class KeyValueEntry : ObservableObject
    {
        [ObservableProperty]
        private string _key = string.Empty;

        /// <summary>The key as a loaded profile held it (null for a row added since): an unedited key saves exactly as loaded.</summary>
        internal string? LoadedKey { get; init; }

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

        /// <summary>The loaded interval's JSON value when it was not a string; saved while <see cref="Every"/> still shows it.</summary>
        internal object? EveryValue { get; init; }

        /// <summary>The loaded metadata values that were not strings; each saved while its entry still shows it.</summary>
        internal IDictionary<string, object?>? MetadataValues { get; init; }

        /// <summary>The manifest entry the card was loaded from; fields the card still shows as loaded are saved as the entry holds them.</summary>
        internal IReadOnlyDictionary<string, object?>? ManifestEntry { get; init; }

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
                EveryValue = EveryValue,
                MetadataValues = MetadataValues,
                ManifestEntry = ManifestEntry,
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

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        RunResults.CollectionChanged -= OnRunResultsChanged;
        Schedules.CollectionChanged -= OnSchedulesCollectionChanged;
        Profiles.CollectionChanged -= OnProfilesCollectionChanged;
        _disposed = true;
    }
}
