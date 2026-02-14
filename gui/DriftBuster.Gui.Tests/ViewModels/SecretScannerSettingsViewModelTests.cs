using DriftBuster.Backend.Models;
using DriftBuster.Gui.ViewModels;

namespace DriftBuster.Gui.Tests.ViewModels;

public sealed class SecretScannerSettingsViewModelTests
{
    [Fact]
    public void BuildResult_trims_and_deduplicates_entries()
    {
        var options = new SecretScannerOptions
        {
            IgnoreRules = new[] { " rule1 ", "rule2", "", "rule2" },
            IgnorePatterns = new[] { "  pattern1", "pattern2", string.Empty },
        };

        var viewModel = new SecretScannerSettingsViewModel(options);
        viewModel.AddRuleCommand.Execute(null);
        viewModel.IgnoreRules[^1].Value = "rule1";
        viewModel.AddPatternCommand.Execute(null);
        viewModel.IgnorePatterns[^1].Value = "pattern2";

        var result = viewModel.BuildResult();

        result.IgnoreRules.Should().Equal("rule1", "rule2");
        result.IgnorePatterns.Should().Equal("pattern1", "pattern2");
    }

    [Fact]
    public void Remove_commands_keep_at_least_one_entry()
    {
        var viewModel = new SecretScannerSettingsViewModel(new SecretScannerOptions());

        viewModel.IgnoreRules.Should().HaveCount(1);
        viewModel.RemoveRuleCommand.CanExecute(viewModel.IgnoreRules[0]).Should().BeFalse();

        viewModel.AddRuleCommand.Execute(null);
        viewModel.IgnoreRules.Should().HaveCount(2);
        viewModel.RemoveRuleCommand.CanExecute(viewModel.IgnoreRules[1]).Should().BeTrue();
        viewModel.RemoveRuleCommand.Execute(viewModel.IgnoreRules[1]);
        viewModel.IgnoreRules.Should().HaveCount(1);

        viewModel.IgnorePatterns.Should().HaveCount(1);
        viewModel.AddPatternCommand.Execute(null);
        viewModel.IgnorePatterns.Should().HaveCount(2);
        viewModel.RemovePatternCommand.Execute(viewModel.IgnorePatterns[0]);
        viewModel.IgnorePatterns.Should().HaveCount(1);
    }

    [Fact]
    public void Constructor_handles_null_arrays_in_options()
    {
        var options = new SecretScannerOptions
        {
            IgnoreRules = null!,
            IgnorePatterns = null!,
        };

        var viewModel = new SecretScannerSettingsViewModel(options);

        viewModel.IgnoreRules.Should().HaveCount(1);
        viewModel.IgnoreRules[0].Value.Should().BeEmpty();
        viewModel.IgnorePatterns.Should().HaveCount(1);
        viewModel.IgnorePatterns[0].Value.Should().BeEmpty();
    }

    [Fact]
    public void RemoveRule_ignores_null_parameter()
    {
        var viewModel = new SecretScannerSettingsViewModel(new SecretScannerOptions());
        viewModel.AddRuleCommand.Execute(null);
        var countBefore = viewModel.IgnoreRules.Count;

        viewModel.RemoveRuleCommand.Execute(null);

        viewModel.IgnoreRules.Should().HaveCount(countBefore);
    }

    [Fact]
    public void RemovePattern_ignores_null_parameter()
    {
        var viewModel = new SecretScannerSettingsViewModel(new SecretScannerOptions());
        viewModel.AddPatternCommand.Execute(null);
        var countBefore = viewModel.IgnorePatterns.Count;

        viewModel.RemovePatternCommand.Execute(null);

        viewModel.IgnorePatterns.Should().HaveCount(countBefore);
    }

    [Fact]
    public void Add_remove_cycle_maintains_minimum_one_entry()
    {
        var viewModel = new SecretScannerSettingsViewModel(new SecretScannerOptions());

        viewModel.AddRuleCommand.Execute(null);
        viewModel.AddRuleCommand.Execute(null);
        viewModel.IgnoreRules.Should().HaveCount(3);

        viewModel.RemoveRuleCommand.Execute(viewModel.IgnoreRules[2]);
        viewModel.RemoveRuleCommand.Execute(viewModel.IgnoreRules[1]);
        viewModel.RemoveRuleCommand.Execute(viewModel.IgnoreRules[0]);
        viewModel.IgnoreRules.Should().HaveCount(1);
    }

    [Fact]
    public void BuildResult_returns_empty_arrays_when_all_entries_blank()
    {
        var viewModel = new SecretScannerSettingsViewModel(new SecretScannerOptions());
        viewModel.IgnoreRules[0].Value = "   ";
        viewModel.IgnorePatterns[0].Value = "";

        var result = viewModel.BuildResult();

        result.IgnoreRules.Should().BeEmpty();
        result.IgnorePatterns.Should().BeEmpty();
    }
}
