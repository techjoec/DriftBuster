using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using DriftBuster.Backend.Models;

namespace DriftBuster.Gui.ViewModels;

public partial class SecretScannerSettingsViewModel : ObservableObject
{
    public ObservableCollection<EditableEntry> IgnoreRules { get; } = new();
    public ObservableCollection<EditableEntry> IgnorePatterns { get; } = new();

    public IRelayCommand AddRuleCommand { get; }
    public IRelayCommand<EditableEntry> RemoveRuleCommand { get; }
    public IRelayCommand AddPatternCommand { get; }
    public IRelayCommand<EditableEntry> RemovePatternCommand { get; }

    public SecretScannerSettingsViewModel(SecretScannerOptions options)
    {
        AddRuleCommand = new RelayCommand(AddRule);
        RemoveRuleCommand = new RelayCommand<EditableEntry>(RemoveRule, entry => entry is not null && IgnoreRules.Count > 1);
        AddPatternCommand = new RelayCommand(AddPattern);
        RemovePatternCommand = new RelayCommand<EditableEntry>(RemovePattern, entry => entry is not null && IgnorePatterns.Count > 1);

        Load(options);
    }

    public SecretScannerOptions BuildResult()
    {
        return new SecretScannerOptions
        {
            IgnoreRules = BuildValues(IgnoreRules),
            IgnorePatterns = BuildValues(IgnorePatterns),
        };
    }

    private static string[] BuildValues(IEnumerable<EditableEntry> entries)
    {
        return entries
            .Select(entry => entry.Value?.Trim())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
    }

    private void Load(SecretScannerOptions options)
    {
        IgnoreRules.Clear();
        foreach (var rule in options.IgnoreRules ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(rule))
            {
                continue;
            }

            IgnoreRules.Add(new EditableEntry { Value = rule });
        }

        if (IgnoreRules.Count == 0)
        {
            IgnoreRules.Add(new EditableEntry());
        }

        IgnorePatterns.Clear();
        foreach (var pattern in options.IgnorePatterns ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(pattern))
            {
                continue;
            }

            IgnorePatterns.Add(new EditableEntry { Value = pattern });
        }

        if (IgnorePatterns.Count == 0)
        {
            IgnorePatterns.Add(new EditableEntry());
        }

        RemoveRuleCommand.NotifyCanExecuteChanged();
        RemovePatternCommand.NotifyCanExecuteChanged();
    }

    private void AddRule()
    {
        IgnoreRules.Add(new EditableEntry());
        RemoveRuleCommand.NotifyCanExecuteChanged();
    }

    private void RemoveRule(EditableEntry? entry)
    {
        if (entry is null)
        {
            return;
        }

        IgnoreRules.Remove(entry);
        if (IgnoreRules.Count == 0)
        {
            IgnoreRules.Add(new EditableEntry());
        }

        RemoveRuleCommand.NotifyCanExecuteChanged();
    }

    private void AddPattern()
    {
        IgnorePatterns.Add(new EditableEntry());
        RemovePatternCommand.NotifyCanExecuteChanged();
    }

    private void RemovePattern(EditableEntry? entry)
    {
        if (entry is null)
        {
            return;
        }

        IgnorePatterns.Remove(entry);
        if (IgnorePatterns.Count == 0)
        {
            IgnorePatterns.Add(new EditableEntry());
        }

        RemovePatternCommand.NotifyCanExecuteChanged();
    }

    public sealed partial class EditableEntry : ObservableObject
    {
        [ObservableProperty]
        private string _value = string.Empty;
    }
}
