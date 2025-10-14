using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using DriftBuster.Gui.Models;
using DriftBuster.Gui.Tests.Fakes;
using DriftBuster.Gui.ViewModels;

using Xunit;

namespace DriftBuster.Gui.Tests.ViewModels;

public class DiffViewModelTests
{
    [Fact]
    public async Task RunDiffCommand_populates_results_and_raw_json()
    {
        var comparison = new DiffComparison
        {
            From = "left.txt",
            To = "right.txt",
            Plan = new DiffPlan
            {
                Before = "before text",
                After = "after text",
                ContentType = "text",
                FromLabel = "left",
                ToLabel = "right",
                Label = "config",
                MaskTokens = new[] { "secret" },
                Placeholder = "[MASKED]",
                ContextLines = 2,
            },
            Metadata = new DiffMetadata
            {
                LeftPath = "left.txt",
                RightPath = "right.txt",
                ContentType = "text",
                ContextLines = 2,
            },
        };

        var service = new FakeDriftbusterService
        {
            DiffResponse = new DiffResult
            {
                Versions = new[] { "left", "right" },
                Comparisons = new[] { comparison },
                RawJson = "{\"comparisons\":[{}]}",
            },
        };

        var left = Path.GetTempFileName();
        var right = Path.GetTempFileName();

        try
        {
            var viewModel = new DiffViewModel(service);
            viewModel.Inputs[0].Path = left;
            viewModel.Inputs[1].Path = right;

            Assert.True(viewModel.RunDiffCommand.CanExecute(null));
            Assert.True(viewModel.ShouldShowPlanHint);

            await viewModel.RunDiffCommand.ExecuteAsync(null);

            Assert.True(viewModel.HasResult);
            Assert.False(viewModel.HasError);
            Assert.Equal("{\"comparisons\":[{}]}", viewModel.RawJson);
            Assert.False(viewModel.ShouldShowPlanHint);

            var comparisonView = Assert.Single(viewModel.Comparisons);
            Assert.Equal("left.txt → right.txt", comparisonView.Title);
            var maskEntry = comparisonView.PlanEntries.Single(p => p.Name == "Mask tokens");
            Assert.Equal("secret", maskEntry.Value);

            service.DiffAsyncHandler = (_, _) => Task.FromException<DiffResult>(new IOException("bad"));

            await viewModel.RunDiffCommand.ExecuteAsync(null);

            Assert.True(viewModel.HasError);
            Assert.Equal("bad", viewModel.ErrorMessage);
            Assert.False(viewModel.HasResult);
            Assert.True(viewModel.ShouldShowPlanHint);
            Assert.Empty(viewModel.Comparisons);
        }
        finally
        {
            File.Delete(left);
            File.Delete(right);
        }
    }

    [Fact]
    public void Validation_flags_missing_files()
    {
        var service = new FakeDriftbusterService();
        var viewModel = new DiffViewModel(service);

        Assert.Equal("Select a baseline file", viewModel.Inputs[0].Error);
        Assert.Null(viewModel.Inputs[1].Error);

        viewModel.Inputs[0].Path = Path.GetTempFileName();
        viewModel.Inputs[1].Path = Path.GetTempFileName();

        try
        {
            Assert.Null(viewModel.Inputs[0].Error);
            Assert.Null(viewModel.Inputs[1].Error);
        }
        finally
        {
            File.Delete(viewModel.Inputs[0].Path!);
            File.Delete(viewModel.Inputs[1].Path!);
        }
    }

    [Fact]
    public async Task Baseline_selection_controls_version_order()
    {
        var left = Path.GetTempFileName();
        var middle = Path.GetTempFileName();
        var right = Path.GetTempFileName();

        File.WriteAllText(left, "left");
        File.WriteAllText(middle, "middle");
        File.WriteAllText(right, "right");

        var captured = new List<string?>();
        var service = new FakeDriftbusterService
        {
            DiffAsyncHandler = (versions, _) =>
            {
                var ordered = versions.Where(v => v is not null).Select(v => v!).ToArray();
                captured = ordered.Cast<string?>().ToList();
                return Task.FromResult(new DiffResult
                {
                    Versions = ordered,
                    Comparisons = Array.Empty<DiffComparison>(),
                });
            },
        };

        try
        {
            var viewModel = new DiffViewModel(service);
            viewModel.Inputs[0].Path = left;
            viewModel.Inputs[1].Path = middle;
            viewModel.AddVersionCommand.Execute(null);
            var third = viewModel.Inputs[2];
            third.Path = right;

            third.IsBaseline = true;

            await viewModel.RunDiffCommand.ExecuteAsync(null);

            Assert.Equal(new[] { right, left, middle }, captured);
        }
        finally
        {
            File.Delete(left);
            File.Delete(middle);
            File.Delete(right);
        }
    }
}
