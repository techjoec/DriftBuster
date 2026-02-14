using System;
using System.Collections.Generic;
using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;

using DriftBuster.Backend.Models;

namespace DriftBuster.Gui.ViewModels
{
    public sealed partial class ConfigCatalogItemViewModel : ObservableObject
    {
        private readonly ConfigCatalogEntry _entry;

        public ConfigCatalogItemViewModel(ConfigCatalogEntry entry, int totalHosts)
        {
            _entry = entry ?? throw new ArgumentNullException(nameof(entry));
            TotalHosts = Math.Max(totalHosts, PresentHosts.Count + MissingHosts.Count);
            if (TotalHosts == 0)
            {
                TotalHosts = Math.Max(1, PresentHosts.Count + MissingHosts.Count);
            }
        }

        public string ConfigId => _entry.ConfigId;

        public string DisplayName => string.IsNullOrWhiteSpace(_entry.DisplayName) ? ConfigId : _entry.DisplayName;

        public string Format => string.IsNullOrWhiteSpace(_entry.Format) ? "unknown" : _entry.Format;

        public int DriftCount => _entry.DriftCount;

        public string Severity => string.IsNullOrWhiteSpace(_entry.Severity) ? "none" : _entry.Severity;

        public int SeverityRank => Severity.ToLowerInvariant() switch
        {
            "high" => 0,
            "medium" => 1,
            "low" => 2,
            "none" => 3,
            _ => 4,
        };

        public IReadOnlyList<string> PresentHosts => _entry.PresentHosts ?? Array.Empty<string>();

        public IReadOnlyList<string> MissingHosts => _entry.MissingHosts ?? Array.Empty<string>();

        public int PresentCount => PresentHosts.Count;

        public int TotalHosts { get; }

        public string CoverageText => TotalHosts <= 0 ? "0/0" : $"{PresentCount}/{TotalHosts}";

        public double CoverageRatio => TotalHosts <= 0 ? 0d : (double)PresentCount / TotalHosts;

        public string CoverageStatus => string.IsNullOrWhiteSpace(_entry.CoverageStatus) ? (IsFullCoverage ? "full" : IsMissingCoverage ? "missing" : "partial") : _entry.CoverageStatus;

        public bool HasDrift => DriftCount > 0;

        public bool HasSecrets => _entry.HasSecrets;

        public bool HasMaskedTokens => _entry.HasMaskedTokens;

        public bool HasValidationIssues => _entry.HasValidationIssues;

        public bool IsFullCoverage => PresentCount >= TotalHosts && TotalHosts > 0;

        public bool IsPartialCoverage => !IsFullCoverage && PresentCount > 0;

        public bool IsMissingCoverage => PresentCount == 0;

        public string DriftSummary => DriftCount > 0 ? $"{DriftCount}" : "—";

        public DateTimeOffset LastUpdated => _entry.LastUpdated;

        public string LastUpdatedText => LastUpdated.ToLocalTime().ToString("g", CultureInfo.InvariantCulture);

        public bool HasAnyAlerts => HasSecrets || HasValidationIssues || HasMaskedTokens || DriftCount > 0;

        public string MissingHostsSummary => MissingHosts.Count == 0 ? "" : string.Join(", ", MissingHosts);
    }
}
