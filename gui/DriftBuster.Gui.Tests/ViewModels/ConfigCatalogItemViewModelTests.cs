using System;
using DriftBuster.Backend.Models;
using DriftBuster.Gui.ViewModels;

namespace DriftBuster.Gui.Tests.ViewModels;

public sealed class ConfigCatalogItemViewModelTests
{
    [Fact]
    public void Constructor_throws_for_null_entry()
    {
        var action = () => new ConfigCatalogItemViewModel(null!, totalHosts: 1);
        action.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Properties_fallback_for_missing_values_and_compute_coverage()
    {
        var entry = new ConfigCatalogEntry
        {
            ConfigId = "cfg-id",
            DisplayName = "",
            Format = "",
            Severity = "",
            PresentHosts = new[] { "server01" },
            MissingHosts = new[] { "server02" },
            DriftCount = 0,
            CoverageStatus = "",
            LastUpdated = DateTimeOffset.UtcNow,
        };

        var viewModel = new ConfigCatalogItemViewModel(entry, totalHosts: 0);

        viewModel.DisplayName.Should().Be("cfg-id");
        viewModel.Format.Should().Be("unknown");
        viewModel.Severity.Should().Be("none");
        viewModel.SeverityRank.Should().Be(3);
        viewModel.CoverageText.Should().Be("1/2");
        viewModel.CoverageRatio.Should().Be(0.5d);
        viewModel.CoverageStatus.Should().Be("partial");
        viewModel.IsPartialCoverage.Should().BeTrue();
        viewModel.IsFullCoverage.Should().BeFalse();
        viewModel.IsMissingCoverage.Should().BeFalse();
        viewModel.DriftSummary.Should().Be("—");
        viewModel.MissingHostsSummary.Should().Be("server02");
    }

    [Fact]
    public void SeverityRank_returns_expected_order_for_known_and_unknown_levels()
    {
        var high = new ConfigCatalogItemViewModel(new ConfigCatalogEntry
        {
            ConfigId = "a",
            Severity = "high",
            PresentHosts = Array.Empty<string>(),
            MissingHosts = Array.Empty<string>(),
        }, totalHosts: 1);
        high.SeverityRank.Should().Be(0);

        var medium = new ConfigCatalogItemViewModel(new ConfigCatalogEntry
        {
            ConfigId = "b",
            Severity = "medium",
            PresentHosts = Array.Empty<string>(),
            MissingHosts = Array.Empty<string>(),
        }, totalHosts: 1);
        medium.SeverityRank.Should().Be(1);

        var low = new ConfigCatalogItemViewModel(new ConfigCatalogEntry
        {
            ConfigId = "c",
            Severity = "low",
            PresentHosts = Array.Empty<string>(),
            MissingHosts = Array.Empty<string>(),
        }, totalHosts: 1);
        low.SeverityRank.Should().Be(2);

        var unknown = new ConfigCatalogItemViewModel(new ConfigCatalogEntry
        {
            ConfigId = "d",
            Severity = "critical-ish",
            PresentHosts = Array.Empty<string>(),
            MissingHosts = Array.Empty<string>(),
        }, totalHosts: 1);
        unknown.SeverityRank.Should().Be(4);
    }

    [Fact]
    public void HasAnyAlerts_is_true_when_entry_has_security_or_validation_flags()
    {
        var entry = new ConfigCatalogEntry
        {
            ConfigId = "cfg",
            HasSecrets = true,
            HasMaskedTokens = true,
            HasValidationIssues = true,
            DriftCount = 1,
            PresentHosts = Array.Empty<string>(),
            MissingHosts = Array.Empty<string>(),
            LastUpdated = DateTimeOffset.UtcNow,
        };

        var viewModel = new ConfigCatalogItemViewModel(entry, totalHosts: 1);

        viewModel.HasAnyAlerts.Should().BeTrue();
        viewModel.HasSecrets.Should().BeTrue();
        viewModel.HasMaskedTokens.Should().BeTrue();
        viewModel.HasValidationIssues.Should().BeTrue();
        viewModel.HasDrift.Should().BeTrue();
        viewModel.DriftSummary.Should().Be("1");
    }
}
