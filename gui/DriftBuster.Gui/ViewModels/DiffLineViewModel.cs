using System.Globalization;

namespace DriftBuster.Gui.ViewModels
{
    /// <summary>
    /// One row of a line diff. In the split layout the left side is the baseline and the right side the comparison; in the
    /// unified layout only the left text is used and the two numbers are the baseline and comparison line numbers.
    /// </summary>
    public sealed class DiffLineViewModel
    {
        public DiffLineViewModel(DiffLineKind kind, int? leftNumber, string leftText, int? rightNumber, string rightText)
        {
            Kind = kind;
            LeftNumber = leftNumber;
            LeftText = leftText;
            RightNumber = rightNumber;
            RightText = rightText;
        }

        public DiffLineKind Kind { get; }

        public int? LeftNumber { get; }

        public string LeftText { get; }

        public int? RightNumber { get; }

        public string RightText { get; }

        public string LeftNumberText => LeftNumber?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

        public string RightNumberText => RightNumber?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

        public bool IsChange => Kind is DiffLineKind.Changed or DiffLineKind.Removed or DiffLineKind.Added;

        /// <summary>The left side has a line that the right side lacks or has differently.</summary>
        public bool LeftDiffers => Kind is DiffLineKind.Changed or DiffLineKind.Removed;

        /// <summary>The right side has a line that the left side lacks or has differently.</summary>
        public bool RightDiffers => Kind is DiffLineKind.Changed or DiffLineKind.Added;

        public bool IsFolded => Kind == DiffLineKind.Folded;

        public bool IsHeader => Kind == DiffLineKind.Header;
    }
}
