using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using DriftBuster.Backend.Diff;

namespace DriftBuster.Gui.ViewModels
{
    /// <summary>
    /// A line diff laid out for reading: either the two files side by side with changed lines aligned, or unified diff text
    /// line by line. Unchanged runs fold down to a few lines of context by default, and previous/next change walks the
    /// changed blocks. The line list is virtualised by the view, so large files stay responsive.
    /// </summary>
    public sealed partial class DiffLinesViewModel : ObservableObject
    {
        /// <summary>Unchanged lines kept around each change when unchanged runs are folded.</summary>
        internal const int ContextLines = 3;

        /// <summary>Unchanged runs this short stay visible instead of folding.</summary>
        internal const int MinFold = 2;

        private readonly IReadOnlyList<DiffLineViewModel> _all;
        private IReadOnlyList<DiffLineViewModel> _lines = Array.Empty<DiffLineViewModel>();
        private IReadOnlyList<int> _changeStarts = Array.Empty<int>();

        private DiffLinesViewModel(IReadOnlyList<DiffLineViewModel> all, bool isSplit, int changeCount)
        {
            _all = all;
            IsSplit = isSplit;
            ChangeCount = changeCount;
            NextChangeCommand = new RelayCommand(() => MoveToChange(forward: true), () => _changeStarts.Count > 0);
            PreviousChangeCommand = new RelayCommand(() => MoveToChange(forward: false), () => _changeStarts.Count > 0);
            Rebuild();
        }

        /// <summary>An empty diff.</summary>
        public static DiffLinesViewModel Empty { get; } = new(Array.Empty<DiffLineViewModel>(), isSplit: false, changeCount: 0);

        /// <summary>The two texts side by side, changed lines aligned by a line diff.</summary>
        public static DiffLinesViewModel FromTexts(string? before, string? after)
        {
            var left = SplitLines(before);
            var right = SplitLines(after);
            var rows = new List<DiffLineViewModel>(Math.Max(left.Count, right.Count));
            var changes = LineDiff.Compare(left, right);
            int l = 0, r = 0;
            foreach (var change in changes)
            {
                while (l < change.BeforeStart)
                {
                    rows.Add(new DiffLineViewModel(DiffLineKind.Same, l + 1, left[l], r + 1, right[r]));
                    l++;
                    r++;
                }

                var paired = Math.Max(change.Removed, change.Inserted);
                for (var index = 0; index < paired; index++)
                {
                    var hasLeft = index < change.Removed;
                    var hasRight = index < change.Inserted;
                    var kind = hasLeft && hasRight ? DiffLineKind.Changed : hasLeft ? DiffLineKind.Removed : DiffLineKind.Added;
                    rows.Add(new DiffLineViewModel(
                        kind,
                        hasLeft ? change.BeforeStart + index + 1 : null,
                        hasLeft ? left[change.BeforeStart + index] : string.Empty,
                        hasRight ? change.AfterStart + index + 1 : null,
                        hasRight ? right[change.AfterStart + index] : string.Empty));
                }

                l = change.BeforeEnd;
                r = change.AfterEnd;
            }

            while (l < left.Count && r < right.Count)
            {
                rows.Add(new DiffLineViewModel(DiffLineKind.Same, l + 1, left[l], r + 1, right[r]));
                l++;
                r++;
            }

            return new DiffLinesViewModel(rows, isSplit: true, changes.Count);
        }

        /// <summary>
        /// Unified diff text line by line: <c>@@ -a,b +c,d @@</c> headers restart the line numbers, <c>-</c> lines are the
        /// baseline's, <c>+</c> lines the comparison's, and file header lines (<c>---</c>, <c>+++</c>) are shown as headers.
        /// </summary>
        public static DiffLinesViewModel FromUnified(string? text)
        {
            var rows = new List<DiffLineViewModel>();
            int left = 0, right = 0, changes = 0;
            var inChange = false;
            foreach (var line in SplitLines(text))
            {
                if (line.StartsWith("@@", StringComparison.Ordinal))
                {
                    (left, right) = HunkStarts(line);
                    rows.Add(new DiffLineViewModel(DiffLineKind.Header, null, line, null, string.Empty));
                    inChange = false;
                    continue;
                }

                if (line.StartsWith("---", StringComparison.Ordinal) || line.StartsWith("+++", StringComparison.Ordinal))
                {
                    rows.Add(new DiffLineViewModel(DiffLineKind.Header, null, line, null, string.Empty));
                    inChange = false;
                    continue;
                }

                var kind = line.Length > 0 && line[0] == '-' ? DiffLineKind.Removed
                    : line.Length > 0 && line[0] == '+' ? DiffLineKind.Added
                    : DiffLineKind.Same;
                if (kind != DiffLineKind.Same && !inChange)
                {
                    changes++;
                }

                inChange = kind != DiffLineKind.Same;
                var body = line.Length > 0 && line[0] is '-' or '+' or ' ' ? line[1..] : line;
                rows.Add(kind switch
                {
                    DiffLineKind.Removed => new DiffLineViewModel(kind, ++left, body, null, string.Empty),
                    DiffLineKind.Added => new DiffLineViewModel(kind, null, body, ++right, string.Empty),
                    _ => new DiffLineViewModel(kind, ++left, body, ++right, string.Empty),
                });
            }

            return new DiffLinesViewModel(rows, isSplit: false, changes);
        }

        public bool IsSplit { get; }

        /// <summary>What the left (baseline) side is called in the column header.</summary>
        public string LeftTitle { get; set; } = "Baseline";

        /// <summary>What the right (comparison) side is called in the column header.</summary>
        public string RightTitle { get; set; } = "Comparison";

        public bool IsUnified => !IsSplit;

        /// <summary>Changed blocks: runs of consecutive changed lines.</summary>
        public int ChangeCount { get; }

        public bool HasLines => _all.Count > 0;

        public bool HasChanges => ChangeCount > 0;

        /// <summary>The rows shown: every row, or with unchanged runs folded to context.</summary>
        public IReadOnlyList<DiffLineViewModel> Lines
        {
            get => _lines;
            private set => SetProperty(ref _lines, value);
        }

        public IRelayCommand NextChangeCommand { get; }

        /// <summary>Raised after previous/next change selects a line, so the view can bring it into view.</summary>
        public event EventHandler? ChangeSelected;

        public IRelayCommand PreviousChangeCommand { get; }

        [ObservableProperty]
        private bool _onlyChanges = true;

        [ObservableProperty]
        private DiffLineViewModel? _selectedLine;

        public string SummaryText => ChangeCount == 0
            ? (HasLines ? "No changes." : string.Empty)
            : string.Create(CultureInfo.InvariantCulture, $"{ChangeCount} {(ChangeCount == 1 ? "change" : "changes")}");

        /// <summary>"Change 3 of 12" once a change is selected, otherwise the change count.</summary>
        public string PositionText
        {
            get
            {
                var index = SelectedLine is null ? -1 : CurrentChangeIndex();
                return index < 0 ? SummaryText : string.Create(CultureInfo.InvariantCulture, $"Change {index + 1} of {_changeStarts.Count}");
            }
        }

        partial void OnOnlyChangesChanged(bool value)
        {
            var selected = SelectedLine;
            Rebuild();
            SelectedLine = selected is not null && Lines.Contains(selected) ? selected : null;
        }

        partial void OnSelectedLineChanged(DiffLineViewModel? value) => OnPropertyChanged(nameof(PositionText));

        private void Rebuild()
        {
            Lines = OnlyChanges && IsSplit ? Fold(_all) : _all;
            var starts = new List<int>();
            for (var index = 0; index < Lines.Count; index++)
            {
                if (Lines[index].IsChange && (index == 0 || !Lines[index - 1].IsChange))
                {
                    starts.Add(index);
                }
            }

            _changeStarts = starts;
            NextChangeCommand.NotifyCanExecuteChanged();
            PreviousChangeCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(PositionText));
        }

        private static List<DiffLineViewModel> Fold(IReadOnlyList<DiffLineViewModel> all)
        {
            var keep = new bool[all.Count];
            for (var index = 0; index < all.Count; index++)
            {
                if (!all[index].IsChange)
                {
                    continue;
                }

                for (var near = Math.Max(0, index - ContextLines); near <= Math.Min(all.Count - 1, index + ContextLines); near++)
                {
                    keep[near] = true;
                }
            }

            var folded = new List<DiffLineViewModel>();
            var index2 = 0;
            while (index2 < all.Count)
            {
                if (keep[index2])
                {
                    folded.Add(all[index2]);
                    index2++;
                    continue;
                }

                var start = index2;
                while (index2 < all.Count && !keep[index2])
                {
                    index2++;
                }

                var count = index2 - start;
                if (count <= MinFold)
                {
                    // Folding a line or two hides as much as it shows; keep them.
                    for (var near = start; near < index2; near++)
                    {
                        folded.Add(all[near]);
                    }

                    continue;
                }

                folded.Add(new DiffLineViewModel(
                    DiffLineKind.Folded,
                    null,
                    string.Create(CultureInfo.InvariantCulture, $"{count} unchanged {(count == 1 ? "line" : "lines")}"),
                    null,
                    string.Empty));
            }

            return folded;
        }

        private int CurrentChangeIndex()
        {
            var position = SelectedLine is null ? -1 : IndexOfSelected();
            if (position < 0 || !Lines[position].IsChange)
            {
                return -1;
            }

            var index = -1;
            for (var change = 0; change < _changeStarts.Count && _changeStarts[change] <= position; change++)
            {
                index = change;
            }

            return index;
        }

        private int IndexOfSelected()
        {
            for (var index = 0; index < Lines.Count; index++)
            {
                if (ReferenceEquals(Lines[index], SelectedLine))
                {
                    return index;
                }
            }

            return -1;
        }

        private void MoveToChange(bool forward)
        {
            if (_changeStarts.Count == 0)
            {
                return;
            }

            var position = SelectedLine is null ? -1 : IndexOfSelected();
            if (position >= 0 && Lines[position].IsChange)
            {
                // Inside a block, "previous" means the block before this one, not this block's start.
                position = BlockStart(position);
            }

            int next;
            if (forward)
            {
                next = _changeStarts.FirstOrDefault(start => start > position, _changeStarts[0]);
            }
            else
            {
                var before = position < 0 ? Lines.Count : position;
                next = _changeStarts.LastOrDefault(start => start < before, _changeStarts[^1]);
            }

            SelectedLine = Lines[next];
            ChangeSelected?.Invoke(this, EventArgs.Empty);
        }

        private int BlockStart(int position)
        {
            while (position > 0 && Lines[position - 1].IsChange)
            {
                position--;
            }

            return position;
        }

        private static (int Left, int Right) HunkStarts(string header)
        {
            // "@@ -a,b +c,d @@": the next removed or context line is line a on the left, the next added or context line is c.
            static int Start(string header, char sign)
            {
                var at = header.IndexOf(sign, 2);
                if (at < 0)
                {
                    return 0;
                }

                var end = at + 1;
                while (end < header.Length && char.IsAsciiDigit(header[end]))
                {
                    end++;
                }

                return int.TryParse(header.AsSpan(at + 1, end - at - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? Math.Max(value - 1, 0) : 0;
            }

            return (Start(header, '-'), Start(header, '+'));
        }

        private static List<string> SplitLines(string? text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return new List<string>();
            }

            var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n').ToList();
            if (lines.Count > 0 && lines[^1].Length == 0)
            {
                lines.RemoveAt(lines.Count - 1);
            }

            return lines;
        }
    }
}
