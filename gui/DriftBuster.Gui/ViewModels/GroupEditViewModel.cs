using System;
using System.Collections.ObjectModel;
using System.Linq;

using CommunityToolkit.Mvvm.ComponentModel;

using DriftBuster.Backend.Curation;

namespace DriftBuster.Gui.ViewModels
{
    /// <summary>A group being edited in Manage choices: its name, description and members.</summary>
    public sealed partial class GroupEditViewModel : ObservableObject
    {
        public GroupEditViewModel(CurationGroup group)
        {
            ArgumentNullException.ThrowIfNull(group);
            _name = group.Name;
            _description = group.Description;
            Scope = group.Scope;
            Members = new ObservableCollection<CurationTargetItem>(group.Members.Select(member => new CurationTargetItem(member)));
        }

        public string Scope { get; }

        [ObservableProperty]
        private string _name;

        [ObservableProperty]
        private string _description;

        public ObservableCollection<CurationTargetItem> Members { get; }

        public CurationGroup ToGroup() => new()
        {
            Name = Name.Trim(),
            Description = Description.Trim(),
            Scope = Scope,
            Members = Members.Select(member => member.Target).ToArray(),
        };
    }
}
