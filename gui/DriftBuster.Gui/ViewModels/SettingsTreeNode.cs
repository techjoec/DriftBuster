using System;
using System.Collections.Generic;
using System.Linq;

namespace DriftBuster.Gui.ViewModels
{
    /// <summary>
    /// A file's settings as a tree: keys split on <c>.</c>, <c>:</c> and <c>/</c> into nested nodes; a leaf shows each server's
    /// value.
    /// </summary>
    public sealed class SettingsTreeNode
    {
        public SettingsTreeNode(string name)
        {
            Name = name;
        }

        public string Name { get; }

        public IList<SettingsTreeNode> Children { get; } = new List<SettingsTreeNode>();

        /// <summary>The setting at this node, when a key ends here.</summary>
        public CompareRowViewModel? Row { get; private set; }

        public string ValuesText => Row is null ? string.Empty : string.Join("  ·  ", Row.Cells.Select(cell => $"{cell.HostLabel}: {cell.Text}"));

        public bool Differs => Row?.RawDiffers == true || Children.Any(child => child.Differs);

        public static IReadOnlyList<SettingsTreeNode> Build(IEnumerable<CompareRowViewModel> rows)
        {
            ArgumentNullException.ThrowIfNull(rows);
            var root = new SettingsTreeNode(string.Empty);
            foreach (var row in rows)
            {
                var node = root;
                foreach (var part in row.Key.Split(['.', ':', '/'], StringSplitOptions.RemoveEmptyEntries))
                {
                    var child = node.Children.FirstOrDefault(existing => string.Equals(existing.Name, part, StringComparison.Ordinal));
                    if (child is null)
                    {
                        child = new SettingsTreeNode(part);
                        node.Children.Add(child);
                    }

                    node = child;
                }

                node.Row = row;
            }

            return root.Children.ToArray();
        }
    }
}
