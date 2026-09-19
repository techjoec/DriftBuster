using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using DriftBuster.Backend.Curation;
using DriftBuster.Gui.Services;

namespace DriftBuster.Gui.ViewModels
{
    /// <summary>
    /// Manage choices: the saved groups, rules, ignore and mask choices and review list, edited together and saved in one go,
    /// plus the scan history's size and import or export of the whole curation.
    /// </summary>
    public sealed partial class CurationManagerViewModel : ObservableObject
    {
        public const int GroupsTab = 0;
        public const int RulesTab = 1;
        public const int ChoicesTab = 2;
        public const int ReviewTab = 3;
        public const int HistoryTab = 4;

        private readonly ICurationService _curation;
        private readonly string _hostSetId;

        public CurationManagerViewModel(ICurationService curation, string hostSetId, int tab = GroupsTab, CurationRule? newRule = null)
        {
            _curation = curation ?? throw new ArgumentNullException(nameof(curation));
            _hostSetId = hostSetId ?? throw new ArgumentNullException(nameof(hostSetId));
            _selectedTab = tab;
            Reload();
            if (newRule is not null)
            {
                var rule = new RuleEditViewModel(newRule);
                Rules.Add(rule);
                SelectedRule = rule;
                IsDirty = true;
            }

            DeleteGroupCommand = new RelayCommand<GroupEditViewModel>(group => Remove(Groups, group));
            RemoveMemberCommand = new RelayCommand<CurationTargetItem>(member =>
            {
                foreach (var group in Groups.Where(group => member is not null && group.Members.Contains(member)))
                {
                    group.Members.Remove(member!);
                    IsDirty = true;
                }
            });
            AddRuleCommand = new RelayCommand(() =>
            {
                var rule = new RuleEditViewModel(new CurationRule { Name = "New rule" });
                Rules.Add(rule);
                SelectedRule = rule;
                IsDirty = true;
            });
            DeleteRuleCommand = new RelayCommand<RuleEditViewModel>(rule => Remove(Rules, rule));
            RemoveChoiceCommand = new RelayCommand<CurationChoiceItem>(choice => Remove(Choices, choice));
            RemoveReviewCommand = new RelayCommand<CurationTargetItem>(item => Remove(Review, item));
            SaveCommand = new RelayCommand(Save);
        }

        public ObservableCollection<GroupEditViewModel> Groups { get; } = new();

        public ObservableCollection<RuleEditViewModel> Rules { get; } = new();

        public ObservableCollection<CurationChoiceItem> Choices { get; } = new();

        public ObservableCollection<CurationTargetItem> Review { get; } = new();

        public IRelayCommand<GroupEditViewModel> DeleteGroupCommand { get; }

        public IRelayCommand<CurationTargetItem> RemoveMemberCommand { get; }

        public IRelayCommand AddRuleCommand { get; }

        public IRelayCommand<RuleEditViewModel> DeleteRuleCommand { get; }

        public IRelayCommand<CurationChoiceItem> RemoveChoiceCommand { get; }

        public IRelayCommand<CurationTargetItem> RemoveReviewCommand { get; }

        public IRelayCommand SaveCommand { get; }

        [ObservableProperty]
        private int _selectedTab;

        [ObservableProperty]
        private GroupEditViewModel? _selectedGroup;

        [ObservableProperty]
        private RuleEditViewModel? _selectedRule;

        /// <summary>True when there are edits not yet saved.</summary>
        [ObservableProperty]
        private bool _isDirty;

        [ObservableProperty]
        private string _statusMessage = string.Empty;

        public string HistoryText
        {
            get
            {
                var stats = _curation.HistoryStats();
                return stats.Runs == 0
                    ? "No scans recorded yet. Each Multi-server run is recorded for the history views."
                    : string.Create(CultureInfo.InvariantCulture, $"{stats.Runs} scans recorded, {stats.FileVersions} distinct file copies, {stats.Bytes / 1024.0 / 1024.0:0.0} MB on disk.");
            }
        }

        public void Save()
        {
            _curation.Update(document => document with
            {
                Groups = Groups.Where(group => group.Name.Trim().Length > 0).Select(group => group.ToGroup()).ToArray(),
                Rules = Rules.Where(rule => rule.Name.Trim().Length > 0).Select(rule => rule.ToRule(_hostSetId)).ToArray(),
                Choices = Choices.Select(choice => choice.Choice).ToArray(),
                Review = document.Review.Where(item => Review.Any(kept => kept.Target == item.Target)).ToArray(),
            });
            IsDirty = false;
            StatusMessage = "Saved.";
        }

        public void ClearHistory()
        {
            _curation.ClearHistory();
            OnPropertyChanged(nameof(HistoryText));
            StatusMessage = "Scan history cleared.";
        }

        public void Export(string path)
        {
            _curation.Export(path);
            StatusMessage = $"Exported to {path}.";
        }

        public void Import(string path, bool replace)
        {
            _curation.Import(path, replace);
            Reload();
            StatusMessage = replace ? $"Replaced with {path}." : $"Merged {path}.";
        }

        private void Reload()
        {
            var document = _curation.Document;
            Groups.Clear();
            foreach (var group in document.Groups)
            {
                Groups.Add(new GroupEditViewModel(group));
            }

            Rules.Clear();
            foreach (var rule in document.Rules)
            {
                Rules.Add(new RuleEditViewModel(rule));
            }

            Choices.Clear();
            foreach (var choice in document.Choices)
            {
                Choices.Add(new CurationChoiceItem(choice));
            }

            Review.Clear();
            foreach (var item in document.Review)
            {
                Review.Add(new CurationTargetItem(item.Target));
            }

            SelectedGroup = Groups.FirstOrDefault();
            SelectedRule = Rules.FirstOrDefault();
            IsDirty = false;
            OnPropertyChanged(nameof(HistoryText));
        }

        private void Remove<T>(ObservableCollection<T> items, T? item)
        {
            if (item is not null && items.Remove(item))
            {
                IsDirty = true;
            }
        }
    }
}
