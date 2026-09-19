using System;

using DriftBuster.Backend.Curation;

namespace DriftBuster.Gui.ViewModels
{
    /// <summary>A curation target (a whole file, a setting, or one value of a setting) in words, for lists.</summary>
    public sealed class CurationTargetItem
    {
        public CurationTargetItem(CurationTarget target, string prefix = "")
        {
            Target = target ?? throw new ArgumentNullException(nameof(target));
            var file = target.File.Length == 0 ? "any file" : target.File;
            var what = target.IsSource ? $"the whole file {file}"
                : target.IsValue ? $"{target.Key} = value {target.ValueHash[..Math.Min(8, target.ValueHash.Length)]}… in {file}"
                : $"{target.Key} in {file}";
            Text = prefix.Length > 0 ? $"{prefix} {what}" : what;
        }

        public CurationTarget Target { get; }

        public string Text { get; }
    }
}
