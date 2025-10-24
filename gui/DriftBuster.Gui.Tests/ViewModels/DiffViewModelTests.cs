using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

using DriftBuster.Backend.Models;
using DriftBuster.Gui.Services;
using DriftBuster.Gui.Tests.Fakes;
using DriftBuster.Gui.ViewModels;

using FluentAssertions;

namespace DriftBuster.Gui.Tests.ViewModels;

public class DiffViewModelTests
{
    [Fact]
    public async Task RunDiffCommand_populates_results_and_raw_json()
    {
        using var temp = new TempDirectory();
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

        var store = new DiffPlannerMruStore(temp.Path);
        var viewModel = CreateViewModel(service, store);
        viewModel.Inputs[0].Path = CreateFile(temp.Path, "left.json", "{}");
        viewModel.Inputs[1].Path = CreateFile(temp.Path, "right.json", "{}");

        viewModel.RunDiffCommand.CanExecute(null).Should().BeTrue();
        viewModel.ShouldShowPlanHint.Should().BeTrue();

        await viewModel.RunDiffCommand.ExecuteAsync(null);

        viewModel.HasResult.Should().BeTrue();
        viewModel.HasError.Should().BeFalse();
        viewModel.RawJson.Should().Be("{\"comparisons\":[{}]}");
        viewModel.HasSanitizedJson.Should().BeFalse();
        viewModel.HasAnyJson.Should().BeTrue();
        viewModel.JsonViewMode.Should().Be(DiffViewModel.DiffJsonViewMode.Raw);
        viewModel.ActiveJson.Should().Be("{\"comparisons\":[{}]}");
        viewModel.ShouldShowPlanHint.Should().BeFalse();

        var comparisonView = viewModel.Comparisons.Should().ContainSingle().Subject;
        comparisonView.Title.Should().Be("left.txt → right.txt");
        comparisonView.PlanEntries.Single(p => p.Name == "Mask tokens").Value.Should().Be("secret");

        service.DiffAsyncHandler = (_, _) => Task.FromException<DiffResult>(new IOException("bad"));

        await viewModel.RunDiffCommand.ExecuteAsync(null);

        viewModel.HasError.Should().BeTrue();
        viewModel.ErrorMessage.Should().Be("bad");
        viewModel.HasResult.Should().BeFalse();
        viewModel.ShouldShowPlanHint.Should().BeTrue();
        viewModel.Comparisons.Should().BeEmpty();
    }

    [Fact]
    public void Validation_flags_missing_files()
    {
        using var temp = new TempDirectory();
        var viewModel = CreateViewModel(new FakeDriftbusterService(), new DiffPlannerMruStore(temp.Path));

        viewModel.Inputs[0].Error.Should().Be("Select a baseline file");
        viewModel.Inputs[1].Error.Should().BeNull();

        viewModel.Inputs[0].Path = CreateFile(temp.Path, "baseline.json", "{}");
        viewModel.Inputs[1].Path = CreateFile(temp.Path, "comparison.json", "{}");

        viewModel.Inputs[0].Error.Should().BeNull();
        viewModel.Inputs[1].Error.Should().BeNull();
    }

    [Fact]
    public async Task RunDiffCommand_prefers_sanitized_payloads()
    {
        using var temp = new TempDirectory();
        var comparison = new DiffComparison
        {
            From = "left", To = "right",
            Plan = new DiffPlan(),
            Metadata = new DiffMetadata(),
        };

        var service = new FakeDriftbusterService
        {
            DiffResponse = new DiffResult
            {
                Comparisons = new[] { comparison },
                RawJson = "{\"raw\":true}",
                SanitizedJson = "{\"safe\":true}",
            },
        };

        var store = new DiffPlannerMruStore(temp.Path);
        var viewModel = CreateViewModel(service, store);
        await viewModel.Initialization;

        viewModel.Inputs[0].Path = CreateFile(temp.Path, "baseline.json", "{}");
        viewModel.Inputs[1].Path = CreateFile(temp.Path, "comparison.json", "{}");

        await viewModel.RunDiffCommand.ExecuteAsync(null);

        viewModel.HasSanitizedJson.Should().BeTrue();
        viewModel.HasAnyJson.Should().BeTrue();
        viewModel.JsonViewMode.Should().Be(DiffViewModel.DiffJsonViewMode.Sanitized);
        viewModel.IsSanitizedViewActive.Should().BeTrue();
        viewModel.ActiveJson.Should().Be("{\"safe\":true}");
        viewModel.CanCopyActiveJson.Should().BeTrue();

        viewModel.SelectJsonViewModeCommand.Execute(DiffViewModel.DiffJsonViewMode.Raw);

        viewModel.IsRawViewActive.Should().BeTrue();
        viewModel.CanCopyActiveJson.Should().BeFalse();

        viewModel.SelectJsonViewModeCommand.Execute(DiffViewModel.DiffJsonViewMode.Sanitized);

        viewModel.IsSanitizedViewActive.Should().BeTrue();
        viewModel.CanCopyActiveJson.Should().BeTrue();
    }

    [Fact]
    public async Task Baseline_selection_controls_version_order()
    {
        using var temp = new TempDirectory();
        var left = CreateFile(temp.Path, "left.txt", "left");
        var middle = CreateFile(temp.Path, "middle.txt", "middle");
        var right = CreateFile(temp.Path, "right.txt", "right");

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

        var viewModel = CreateViewModel(service, new DiffPlannerMruStore(temp.Path));
        viewModel.Inputs[0].Path = left;
        viewModel.Inputs[1].Path = middle;
        viewModel.AddVersionCommand.Execute(null);
        var third = viewModel.Inputs[2];
        third.Path = right;

        third.IsBaseline = true;

        await viewModel.RunDiffCommand.ExecuteAsync(null);

        captured.Should().Equal(new[] { right, left, middle });
    }

    [Fact]
    public void AddVersionCommand_enforces_limit()
    {
        using var temp = new TempDirectory();
        var viewModel = CreateViewModel(new FakeDriftbusterService(), new DiffPlannerMruStore(temp.Path));
        viewModel.Inputs[0].Path = CreateFile(temp.Path, "baseline.json", "{}");
        viewModel.Inputs[1].Path = CreateFile(temp.Path, "comparison-0.json", "{}");

        for (var index = 0; index < 3; index++)
        {
            viewModel.AddVersionCommand.Execute(null);
            viewModel.Inputs[^1].Path = CreateFile(temp.Path, $"comparison-{index + 1}.json", "{}");
        }

        viewModel.Inputs.Count.Should().Be(5);
        viewModel.AddVersionCommand.Execute(null);
        viewModel.Inputs.Count.Should().Be(5);
    }

    [Fact]
    public void RemoveVersionCommand_requires_two_entries()
    {
        using var temp = new TempDirectory();
        var viewModel = CreateViewModel(new FakeDriftbusterService(), new DiffPlannerMruStore(temp.Path));
        var removable = viewModel.Inputs[1];
        viewModel.RemoveVersionCommand.CanExecute(removable).Should().BeFalse();

        viewModel.AddVersionCommand.Execute(null);
        var extra = viewModel.Inputs[2];
        viewModel.RemoveVersionCommand.CanExecute(extra).Should().BeTrue();
        viewModel.RemoveVersionCommand.Execute(extra);
        viewModel.Inputs.Count.Should().Be(2);
    }

    [Fact]
    public async Task RunDiffAsync_requires_baseline_path()
    {
        using var temp = new TempDirectory();
        var viewModel = CreateViewModel(new FakeDriftbusterService(), new DiffPlannerMruStore(temp.Path));
        viewModel.Inputs[1].Path = CreateFile(temp.Path, "comparison.json", "{}");

        await InvokeRunDiffAsync(viewModel);

        viewModel.ErrorMessage.Should().Be("Select a baseline file");
    }

    [Fact]
    public async Task RunDiffAsync_requires_comparison_file()
    {
        using var temp = new TempDirectory();
        var viewModel = CreateViewModel(new FakeDriftbusterService(), new DiffPlannerMruStore(temp.Path));
        viewModel.Inputs[0].Path = CreateFile(temp.Path, "baseline.json", "{}");

        await InvokeRunDiffAsync(viewModel);

        viewModel.ErrorMessage.Should().Be("Select at least one comparison file.");
    }

    [Fact]
    public async Task SelectedMruEntry_populates_inputs()
    {
        using var temp = new TempDirectory();
        var store = new DiffPlannerMruStore(temp.Path);
        var baseline = CreateFile(temp.Path, "baseline.json", "{}");
        var comparisonA = CreateFile(temp.Path, "comparison-a.json", "{}");
        var comparisonB = CreateFile(temp.Path, "comparison-b.json", "{}");

        var snapshot = new DiffPlannerMruSnapshot
        {
            Entries =
            {
                new DiffPlannerMruEntry
                {
                    BaselinePath = baseline,
                    ComparisonPaths = new List<string> { comparisonA, comparisonB },
                    DisplayName = "Example entry",
                    PayloadKind = DiffPlannerPayloadKind.Raw,
                    LastUsedUtc = DateTimeOffset.UtcNow,
                },
            },
        };

        await store.SaveAsync(snapshot);

        var viewModel = CreateViewModel(new FakeDriftbusterService(), store);
        viewModel.MruEntries.Should().HaveCount(1);

        viewModel.SelectedMruEntry = viewModel.MruEntries[0];

        viewModel.Inputs[0].Path.Should().Be(baseline);
        viewModel.Inputs[1].Path.Should().Be(comparisonA);
        viewModel.Inputs[2].Path.Should().Be(comparisonB);
    }

    [Fact]
    public async Task RunDiffCommand_records_mru_entry_with_sanitized_payload()
    {
        using var temp = new TempDirectory();
        var baseline = CreateFile(temp.Path, "baseline.json", "{}");
        var comparison = CreateFile(temp.Path, "comparison.json", "{}");

        var service = new FakeDriftbusterService
        {
            DiffResponse = new DiffResult
            {
                Comparisons = new[]
                {
                    new DiffComparison
                    {
                        From = baseline,
                        To = comparison,
                        Plan = new DiffPlan(),
                        Metadata = new DiffMetadata(),
                    },
                },
                RawJson = "{\"raw\":true}",
                SanitizedJson = "{\"sanitized\":true}",
            },
        };

        var store = new DiffPlannerMruStore(temp.Path);
        var viewModel = CreateViewModel(service, store, () => new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero));
        viewModel.Inputs[0].Path = baseline;
        viewModel.Inputs[1].Path = comparison;

        await viewModel.RunDiffCommand.ExecuteAsync(null);

        viewModel.JsonViewMode.Should().Be(DiffViewModel.DiffJsonViewMode.Sanitized);
        viewModel.ActiveJson.Should().Be("{\"sanitized\":true}");
        viewModel.SelectedMruEntry.Should().NotBeNull();
        viewModel.SelectedMruEntry!.Entry.BaselinePath.Should().Be(baseline);
        viewModel.SelectJsonViewModeCommand.CanExecute(DiffViewModel.DiffJsonViewMode.Sanitized).Should().BeTrue();

        var snapshot = await store.LoadAsync();
        snapshot.Entries.Should().ContainSingle();
        var entry = snapshot.Entries[0];
        entry.BaselinePath.Should().Be(baseline);
        entry.ComparisonPaths.Should().Equal(comparison);
        entry.PayloadKind.Should().Be(DiffPlannerPayloadKind.Sanitized);
        entry.SanitizedDigest.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task JsonViewMode_defaults_to_raw_when_sanitized_missing()
    {
        using var temp = new TempDirectory();
        var baseline = CreateFile(temp.Path, "baseline.json", "{}");
        var comparison = CreateFile(temp.Path, "comparison.json", "{}");

        var service = new FakeDriftbusterService
        {
            DiffResponse = new DiffResult
            {
                Comparisons = Array.Empty<DiffComparison>(),
                RawJson = "{\"raw\":true}",
            },
        };

        var store = new DiffPlannerMruStore(temp.Path);
        var viewModel = CreateViewModel(service, store);
        viewModel.Inputs[0].Path = baseline;
        viewModel.Inputs[1].Path = comparison;

        await viewModel.RunDiffCommand.ExecuteAsync(null);

        viewModel.HasSanitizedJson.Should().BeFalse();
        viewModel.JsonViewMode.Should().Be(DiffViewModel.DiffJsonViewMode.Raw);
        viewModel.ActiveJson.Should().Be("{\"raw\":true}");
        viewModel.SelectJsonViewModeCommand.CanExecute(DiffViewModel.DiffJsonViewMode.Sanitized).Should().BeFalse();

        var snapshot = await store.LoadAsync();
        snapshot.Entries.Should().ContainSingle();
        snapshot.Entries[0].PayloadKind.Should().Be(DiffPlannerPayloadKind.Raw);
        snapshot.Entries[0].SanitizedDigest.Should().BeNull();
    }

    [Fact]
    public async Task RunDiffCommand_exposes_sanitized_summary_payload()
    {
        using var temp = new TempDirectory();
        var baseline = CreateFile(temp.Path, "baseline.json", "{}");
        var comparison = CreateFile(temp.Path, "comparison.json", "{}");

        var sanitizedPayload = "{\"generated_at\":\"2024-01-01T00:00:00+00:00\",\"versions\":[\"baseline.json\",\"comparison.json\"],\"comparison_count\":1,\"comparisons\":[{\"from\":\"baseline.json\",\"to\":\"comparison.json\",\"plan\":{\"content_type\":\"text\",\"from_label\":\"baseline\",\"to_label\":\"comparison\",\"label\":null,\"mask_tokens\":[],\"placeholder\":\"[REDACTED]\",\"context_lines\":3},\"metadata\":{\"content_type\":\"text\",\"context_lines\":3,\"baseline_name\":\"baseline.json\",\"comparison_name\":\"comparison.json\"},\"summary\":{\"before_digest\":\"sha256:111\",\"after_digest\":\"sha256:222\",\"diff_digest\":\"sha256:333\",\"before_lines\":1,\"after_lines\":1,\"added_lines\":0,\"removed_lines\":0,\"changed_lines\":1}}]}";

        var service = new FakeDriftbusterService
        {
            DiffResponse = new DiffResult
            {
                Comparisons = new[]
                {
                    new DiffComparison
                    {
                        From = baseline,
                        To = comparison,
                        Plan = new DiffPlan(),
                        Metadata = new DiffMetadata(),
                    },
                },
                RawJson = "{}",
                SanitizedJson = sanitizedPayload,
            },
        };

        var store = new DiffPlannerMruStore(temp.Path);
        var viewModel = CreateViewModel(service, store);
        viewModel.Inputs[0].Path = baseline;
        viewModel.Inputs[1].Path = comparison;

        await viewModel.RunDiffCommand.ExecuteAsync(null);

        viewModel.HasSanitizedJson.Should().BeTrue();
        using var document = JsonDocument.Parse(viewModel.SanitizedJson);
        var root = document.RootElement;
        root.GetProperty("comparison_count").GetInt32().Should().Be(1);
        var summary = root.GetProperty("comparisons")[0].GetProperty("summary");
        summary.GetProperty("before_digest").GetString().Should().Be("sha256:111");
    }

    private static Task InvokeRunDiffAsync(DiffViewModel viewModel)
    {
        var method = typeof(DiffViewModel).GetMethod(
            "RunDiffAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        method.Should().NotBeNull();
        return (Task)method!.Invoke(viewModel, Array.Empty<object>())!;
    }

    private static DiffViewModel CreateViewModel(
        IDriftbusterService service,
        DiffPlannerMruStore store,
        Func<DateTimeOffset>? clock = null)
    {
        var viewModel = new DiffViewModel(service, store, clock);
        viewModel.Initialization.GetAwaiter().GetResult();
        return viewModel;
    }

    private static string CreateFile(string directory, string name, string content)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, content);
        return path;
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "DriftBusterTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch
            {
            }
        }
    }
}
