using DriftBuster.Gui.ViewModels;

namespace DriftBuster.Gui.Tests.ViewModels;

/// <summary>Line diffs laid out for reading: aligned side by side, folded to context, unified text, and change navigation.</summary>
public sealed class DiffLinesViewModelTests
{
    private static string Lines(int count, Func<int, string> line) => string.Join("\n", Enumerable.Range(1, count).Select(line)) + "\n";

    [Fact]
    public void Aligns_changed_lines_side_by_side()
    {
        var diff = DiffLinesViewModel.FromTexts("a\nb\nc\n", "a\nB\nc\nd\n");
        diff.OnlyChanges = false;

        diff.IsSplit.Should().BeTrue();
        diff.ChangeCount.Should().Be(2);
        diff.Lines.Select(line => line.Kind).Should().Equal(DiffLineKind.Same, DiffLineKind.Changed, DiffLineKind.Same, DiffLineKind.Added);
        var changed = diff.Lines[1];
        (changed.LeftNumberText, changed.LeftText, changed.RightNumberText, changed.RightText).Should().Be(("2", "b", "2", "B"));
        diff.Lines[3].LeftNumber.Should().BeNull();
        diff.Lines[3].RightDiffers.Should().BeTrue();
        diff.Lines[3].LeftDiffers.Should().BeFalse();
    }

    [Fact]
    public void Folds_unchanged_runs_down_to_context()
    {
        var before = Lines(40, i => $"line {i}");
        var after = before.Replace("line 20\n", "line twenty\n", StringComparison.Ordinal);
        var diff = DiffLinesViewModel.FromTexts(before, after);

        diff.Lines.Select(line => line.Kind).Should().Equal(
            [DiffLineKind.Folded, .. Enumerable.Repeat(DiffLineKind.Same, 3), DiffLineKind.Changed, .. Enumerable.Repeat(DiffLineKind.Same, 3), DiffLineKind.Folded]);
        diff.Lines[0].LeftText.Should().Be("16 unchanged lines");
        diff.Lines[^1].LeftText.Should().Be("17 unchanged lines");

        diff.OnlyChanges = false;
        diff.Lines.Should().HaveCount(40);
    }

    [Fact]
    public void Keeps_short_unchanged_runs_between_changes()
    {
        var before = Lines(20, i => $"line {i}");
        var after = before.Replace("line 5\n", "five\n", StringComparison.Ordinal).Replace("line 12\n", "twelve\n", StringComparison.Ordinal);
        var diff = DiffLinesViewModel.FromTexts(before, after);

        diff.Lines.Count(line => line.IsFolded).Should().Be(1, "only the five trailing lines fold");
        diff.Lines[0].LeftText.Should().Be("line 1", "a single leading line is not worth folding");
        diff.Lines[^1].LeftText.Should().Be("5 unchanged lines");
    }

    [Fact]
    public void Walks_changes_forwards_and_backwards_and_wraps()
    {
        var before = Lines(30, i => $"line {i}");
        var after = before.Replace("line 5\n", "five\n", StringComparison.Ordinal).Replace("line 25\n", "twenty-five\n", StringComparison.Ordinal);
        var diff = DiffLinesViewModel.FromTexts(before, after);

        diff.PositionText.Should().Be("2 changes");
        diff.NextChangeCommand.Execute(null);
        diff.SelectedLine!.LeftText.Should().Be("line 5");
        diff.PositionText.Should().Be("Change 1 of 2");

        diff.NextChangeCommand.Execute(null);
        diff.SelectedLine!.LeftText.Should().Be("line 25");
        diff.NextChangeCommand.Execute(null);
        diff.SelectedLine!.LeftText.Should().Be("line 5", "the walk wraps around");

        diff.PreviousChangeCommand.Execute(null);
        diff.SelectedLine!.LeftText.Should().Be("line 25");
        diff.PreviousChangeCommand.Execute(null);
        diff.SelectedLine!.LeftText.Should().Be("line 5");

        diff.OnlyChanges = false;
        diff.SelectedLine!.LeftText.Should().Be("line 5", "the selection survives unfolding");
    }

    [Fact]
    public void Reads_unified_diff_text_with_line_numbers()
    {
        const string text = "--- a/web.config\n+++ b/web.config\n@@ -3,3 +3,3 @@\n keep\n-old\n+new\n tail\n";
        var diff = DiffLinesViewModel.FromUnified(text);

        diff.IsUnified.Should().BeTrue();
        diff.ChangeCount.Should().Be(1);
        diff.Lines.Select(line => line.Kind).Should().Equal(
            DiffLineKind.Header, DiffLineKind.Header, DiffLineKind.Header, DiffLineKind.Same, DiffLineKind.Removed, DiffLineKind.Added, DiffLineKind.Same);
        diff.Lines[3].LeftText.Should().Be("keep");
        (diff.Lines[3].LeftNumber, diff.Lines[3].RightNumber).Should().Be((3, 3));
        (diff.Lines[4].LeftNumber, diff.Lines[4].RightNumber).Should().Be((4, null));
        (diff.Lines[5].LeftNumber, diff.Lines[5].RightNumber).Should().Be((null, 4));
        (diff.Lines[6].LeftNumber, diff.Lines[6].RightNumber).Should().Be((5, 5));

        diff.NextChangeCommand.Execute(null);
        diff.SelectedLine!.LeftText.Should().Be("old");
    }

    [Fact]
    public void Empty_and_identical_inputs_have_nothing_to_walk()
    {
        DiffLinesViewModel.Empty.HasLines.Should().BeFalse();
        DiffLinesViewModel.Empty.SummaryText.Should().BeEmpty();

        var same = DiffLinesViewModel.FromTexts("a\n", "a\n");
        same.SummaryText.Should().Be("No changes.");
        same.NextChangeCommand.CanExecute(null).Should().BeFalse();
        DiffLinesViewModel.FromUnified(null).Lines.Should().BeEmpty();
    }
}
