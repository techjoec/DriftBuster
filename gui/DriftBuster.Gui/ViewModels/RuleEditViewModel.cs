using System;
using System.Collections.Generic;
using System.Linq;

using CommunityToolkit.Mvvm.ComponentModel;

using DriftBuster.Backend.Curation;

namespace DriftBuster.Gui.ViewModels
{
    /// <summary>A rule being edited in Manage choices; <see cref="ToRule"/> turns it back into a saved rule.</summary>
    public sealed partial class RuleEditViewModel : ObservableObject
    {
        public static IReadOnlyList<string> MaskOptions { get; } = ["No change", "Mask values", "Show values"];

        public RuleEditViewModel(CurationRule rule)
        {
            ArgumentNullException.ThrowIfNull(rule);
            _name = rule.Name;
            _enabled = rule.Enabled;
            _theseServersOnly = rule.Scope.Length > 0;
            Scope = rule.Scope;
            _filePattern = rule.FilePattern;
            _keyPattern = rule.KeyPattern;
            _appName = rule.AppName;
            _fileLabel = rule.FileLabel;
            _description = rule.Description;
            _ignore = rule.Ignore;
            _maskOption = rule.Mask switch { CurationChoiceKinds.Mask => MaskOptions[1], CurationChoiceKinds.Unmask => MaskOptions[2], _ => MaskOptions[0] };
            _groups = string.Join(", ", rule.Groups);
        }

        /// <summary>The host set the rule was saved for, kept when "these servers only" stays on.</summary>
        public string Scope { get; }

        [ObservableProperty]
        private string _name;

        [ObservableProperty]
        private bool _enabled;

        [ObservableProperty]
        private bool _theseServersOnly;

        [ObservableProperty]
        private string _filePattern;

        [ObservableProperty]
        private string _keyPattern;

        [ObservableProperty]
        private string _appName;

        [ObservableProperty]
        private string _fileLabel;

        [ObservableProperty]
        private string _description;

        [ObservableProperty]
        private bool _ignore;

        [ObservableProperty]
        private string _maskOption;

        /// <summary>Group names, comma separated.</summary>
        [ObservableProperty]
        private string _groups;

        public CurationRule ToRule(string hostSetId) => new()
        {
            Name = Name.Trim(),
            Enabled = Enabled,
            Scope = TheseServersOnly ? (Scope.Length > 0 ? Scope : hostSetId) : CurationScopes.AllRuns,
            FilePattern = FilePattern.Trim(),
            KeyPattern = KeyPattern.Trim(),
            AppName = AppName.Trim(),
            FileLabel = FileLabel.Trim(),
            Description = Description.Trim(),
            Ignore = Ignore,
            Mask = string.Equals(MaskOption, MaskOptions[1], StringComparison.Ordinal) ? CurationChoiceKinds.Mask : string.Equals(MaskOption, MaskOptions[2], StringComparison.Ordinal) ? CurationChoiceKinds.Unmask : string.Empty,
            Groups = Groups.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
        };
    }
}
