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
}
