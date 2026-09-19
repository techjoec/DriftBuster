using CommunityToolkit.Mvvm.ComponentModel;

namespace DriftBuster.Gui.ViewModels
{
    /// <summary>One rule in the Hunt summary strip: its name, how many findings it has, and whether it is the rule shown.</summary>
    public sealed partial class HuntRuleChip : ObservableObject
    {
        public HuntRuleChip(string name, int count)
        {
            Name = name;
            Count = count;
        }

        public string Name { get; }

        public int Count { get; }

        [ObservableProperty]
        private bool _isSelected;
    }
}
