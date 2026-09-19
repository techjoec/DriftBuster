namespace DriftBuster.Gui.ViewModels
{
    /// <summary>The Compare view's filters, applied to every file.</summary>
    public sealed record CompareFilter
    {
        public bool DifferencesOnly { get; init; } = true;

        public string? FocusHostId { get; init; }

        public string Search { get; init; } = string.Empty;

        /// <summary>Show ignored files and settings (dimmed) instead of hiding them.</summary>
        public bool ShowIgnored { get; init; }

        public CompareMarkFilter Marks { get; init; } = CompareMarkFilter.All;

        /// <summary>Show only this group's settings; null for every setting.</summary>
        public string? Group { get; init; }

        /// <summary>Show only settings on the review list.</summary>
        public bool ReviewOnly { get; init; }

        /// <summary>True when a filter narrows to chosen settings, so a file shows only when one of its settings passes.</summary>
        public bool Narrows => ReviewOnly || Group is not null || Marks != CompareMarkFilter.All;
    }
}
