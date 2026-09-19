using System;

using DriftBuster.Backend.Curation;

namespace DriftBuster.Gui.ViewModels
{
    /// <summary>A saved ignore, mask or unmask choice in words, for Manage choices.</summary>
    public sealed class CurationChoiceItem
    {
        public CurationChoiceItem(CurationChoice choice)
        {
            Choice = choice ?? throw new ArgumentNullException(nameof(choice));
            var verb = choice.Kind switch { CurationChoiceKinds.Mask => "Mask", CurationChoiceKinds.Unmask => "Show", _ => "Ignore" };
            var when = choice.Scope.Length == 0 ? "every run" : "these servers only";
            var target = choice.Target.IsValue && choice.Note.Length > 0
                ? $"{verb} {choice.Target.Key} = {choice.Note} in {(choice.Target.File.Length == 0 ? "any file" : choice.Target.File)}"
                : new CurationTargetItem(choice.Target, verb).Text;
            Text = $"{target} ({when})";
        }

        public CurationChoice Choice { get; }

        public string Text { get; }
    }
}
