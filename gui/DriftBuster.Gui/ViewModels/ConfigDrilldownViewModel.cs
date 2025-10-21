using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using DriftBuster.Backend.Models;

namespace DriftBuster.Gui.ViewModels
{
    public enum DiffViewMode
    {
        SideBySide,
        Unified,
    }

    public sealed partial class ConfigDrilldownServerViewModel : ObservableObject
    {
        public ConfigDrilldownServerViewModel(ConfigServerDetail detail)
        {
            Detail = detail ?? throw new ArgumentNullException(nameof(detail));
            _isSelected = detail.Present;
        }

        public ConfigServerDetail Detail { get; }

        [ObservableProperty]
        private bool _isSelected;

        public string HostId => Detail.HostId;

        public string Label => Detail.Label;

        public bool Present => Detail.Present;

        public bool IsBaseline => Detail.IsBaseline;

        public string Status => Detail.Status;

        public int DriftLineCount => Detail.DriftLineCount;

        public bool HasSecrets => Detail.HasSecrets;

        public bool Masked => Detail.Masked;

        public string RedactionStatus => Detail.RedactionStatus;

        public DateTimeOffset LastSeen => Detail.LastSeen;

        public string LastSeenText => Detail.LastSeen == DateTimeOffset.MinValue
            ? "Not scanned"
            : Detail.LastSeen.ToLocalTime().ToString("g");
    }

    public sealed partial class ConfigDrilldownViewModel : ObservableObject
    {
        private readonly ConfigDrilldown _source;

        public ConfigDrilldownViewModel(ConfigDrilldown source)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));

            Servers = new ObservableCollection<ConfigDrilldownServerViewModel>(
                (_source.Servers ?? Array.Empty<ConfigServerDetail>()).Select(detail => new ConfigDrilldownServerViewModel(detail)));
            Servers.CollectionChanged += OnServersCollectionChanged;
            foreach (var server in Servers)
            {
                AttachServerHandlers(server);
            }

            DiffMode = DiffViewMode.SideBySide;
            UnifiedDiff = string.IsNullOrWhiteSpace(_source.UnifiedDiff)
                ? BuildUnifiedDiff(_source.DiffBefore, _source.DiffAfter)
                : _source.UnifiedDiff;

            BackCommand = new RelayCommand(() => BackRequested?.Invoke(this, EventArgs.Empty));
            ExportHtmlCommand = new AsyncRelayCommand(() => RaiseExportAsync(ExportFormat.Html));
            ExportJsonCommand = new AsyncRelayCommand(() => RaiseExportAsync(ExportFormat.Json));
            ReScanSelectedCommand = new RelayCommand(RaiseReScanRequested, () => Servers.Any(server => server.IsSelected));
            SelectAllCommand = new RelayCommand(() => SetSelection(true));
            SelectNoneCommand = new RelayCommand(() => SetSelection(false));
            ToggleModeCommand = new RelayCommand<DiffViewMode>(mode => DiffMode = mode);
        }

        public ObservableCollection<ConfigDrilldownServerViewModel> Servers { get; }

        [ObservableProperty]
        private DiffViewMode _diffMode;

        public IReadOnlyList<DiffViewMode> DiffModes { get; } = Enum.GetValues<DiffViewMode>();

        partial void OnDiffModeChanged(DiffViewMode value)
        {
            OnPropertyChanged(nameof(IsSideBySide));
            OnPropertyChanged(nameof(IsUnified));
        }

        public bool IsSideBySide => DiffMode == DiffViewMode.SideBySide;

        public bool IsUnified => DiffMode == DiffViewMode.Unified;

        public string ConfigId => _source.ConfigId;

        public string DisplayName => string.IsNullOrWhiteSpace(_source.DisplayName) ? ConfigId : _source.DisplayName;

        public string Format => string.IsNullOrWhiteSpace(_source.Format) ? "unknown" : _source.Format;

        public string DiffBefore => _source.DiffBefore;

        public string DiffAfter => _source.DiffAfter;

        public string UnifiedDiff { get; }

        public bool HasSecrets => _source.HasSecrets;

        public bool HasMaskedTokens => _source.HasMaskedTokens;

        public bool HasValidationIssues => _source.HasValidationIssues;

        public int DriftCount => _source.DriftCount;

        public DateTimeOffset LastUpdated => _source.LastUpdated;

        public string LastUpdatedText => LastUpdated.ToLocalTime().ToString("g");

        public string Provenance => string.IsNullOrWhiteSpace(_source.Provenance) ? "Unknown provenance" : _source.Provenance;

        public IReadOnlyList<string> Notes => _source.Notes ?? Array.Empty<string>();

        public string BaselineHostId => _source.BaselineHostId;

        public ConfigDrilldownServerViewModel? BaselineServer => Servers.FirstOrDefault(server => server.HostId == BaselineHostId);

        public IRelayCommand BackCommand { get; }

        public IAsyncRelayCommand ExportHtmlCommand { get; }

        public IAsyncRelayCommand ExportJsonCommand { get; }

        public IRelayCommand ReScanSelectedCommand { get; }

        public IRelayCommand SelectAllCommand { get; }

        public IRelayCommand SelectNoneCommand { get; }

        public IRelayCommand ToggleModeCommand { get; }

        public event EventHandler? BackRequested;

        public event EventHandler<ConfigDrilldownExportRequest>? ExportRequested;

        public event EventHandler<IReadOnlyList<string>>? ReScanRequested;

        private void SetSelection(bool value)
        {
            foreach (var server in Servers)
            {
                server.IsSelected = value;
            }

            ReScanSelectedCommand.NotifyCanExecuteChanged();
        }

        private void OnServersCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.OldItems is not null)
            {
                foreach (ConfigDrilldownServerViewModel server in e.OldItems)
                {
                    DetachServerHandlers(server);
                }
            }

            if (e.NewItems is not null)
            {
                foreach (ConfigDrilldownServerViewModel server in e.NewItems)
                {
                    AttachServerHandlers(server);
                }
            }

            ReScanSelectedCommand.NotifyCanExecuteChanged();
        }

        private void AttachServerHandlers(ConfigDrilldownServerViewModel server)
        {
            server.PropertyChanged += OnServerPropertyChanged;
        }

        private void DetachServerHandlers(ConfigDrilldownServerViewModel server)
        {
            server.PropertyChanged -= OnServerPropertyChanged;
        }

        private void OnServerPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (string.Equals(e.PropertyName, nameof(ConfigDrilldownServerViewModel.IsSelected), StringComparison.Ordinal))
            {
                ReScanSelectedCommand.NotifyCanExecuteChanged();
            }
        }

        private void RaiseReScanRequested()
        {
            var selected = Servers
                .Where(server => server.IsSelected)
                .Select(server => string.IsNullOrWhiteSpace(server.Detail.Label) ? server.HostId : server.Detail.Label)
                .ToArray();

            if (selected.Length == 0)
            {
                return;
            }

            ReScanRequested?.Invoke(this, selected);
        }

        private Task RaiseExportAsync(ExportFormat format)
        {
            var payload = format switch
            {
                ExportFormat.Json => BuildJsonPayload(),
                ExportFormat.Html => BuildHtmlPayload(),
                _ => string.Empty,
            };

            ExportRequested?.Invoke(this, new ConfigDrilldownExportRequest(DisplayName, ConfigId, format, payload));
            return Task.CompletedTask;
        }

        private string BuildJsonPayload()
        {
            var exportModel = new DrilldownExportSnapshot
            {
                ConfigId = ConfigId,
                DisplayName = DisplayName,
                Format = Format,
                DriftCount = DriftCount,
                LastUpdated = LastUpdated,
                HasSecrets = HasSecrets,
                HasMaskedTokens = HasMaskedTokens,
                HasValidationIssues = HasValidationIssues,
                Notes = Notes,
                Provenance = Provenance,
                DiffBefore = DiffBefore,
                DiffAfter = DiffAfter,
                UnifiedDiff = UnifiedDiff,
                Servers = Servers.Select(server => new DrilldownExportServer
                {
                    HostId = server.HostId,
                    Label = server.Label,
                    Present = server.Present,
                    IsBaseline = server.IsBaseline,
                    Status = server.Status,
                    DriftLineCount = server.DriftLineCount,
                    HasSecrets = server.HasSecrets,
                    Masked = server.Masked,
                    RedactionStatus = server.RedactionStatus,
                    LastSeen = server.LastSeen,
                }).ToList(),
            };

            return JsonSerializer.Serialize(exportModel, new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            });
        }

        private string BuildHtmlPayload()
        {
            var notes = Notes.Count > 0 ? string.Join("", Notes.Select(note => $"<li>{System.Net.WebUtility.HtmlEncode(note)}</li>")) : "<li>No notes</li>";
            var serverRows = string.Join(string.Empty, Servers.Select(server =>
                $"<tr><td>{System.Net.WebUtility.HtmlEncode(server.Label)}</td><td>{server.Status}</td><td>{server.DriftLineCount}</td><td>{(server.Present ? "Yes" : "No")}</td></tr>"));

            var builder = new System.Text.StringBuilder();
            builder.AppendLine("<!DOCTYPE html>");
            builder.AppendLine("<html lang=\"en\">");
            builder.AppendLine("<head>");
            builder.AppendLine("  <meta charset=\"utf-8\" />");
            builder.AppendLine($"  <title>Drilldown – {System.Net.WebUtility.HtmlEncode(DisplayName)}</title>");
            builder.AppendLine("  <style>");
            builder.AppendLine("    body { font-family: Segoe UI, sans-serif; margin: 2rem; }");
            builder.AppendLine("    table { border-collapse: collapse; width: 100%; margin-top: 1rem; }");
            builder.AppendLine("    th, td { border: 1px solid #d1d5db; padding: 0.5rem; text-align: left; }");
            builder.AppendLine("    th { background: #f1f5f9; }");
            builder.AppendLine("    pre { background: #0f172a; color: #e2e8f0; padding: 1rem; border-radius: 8px; overflow-x: auto; }");
            builder.AppendLine("  </style>");
            builder.AppendLine("</head>");
            builder.AppendLine("<body>");
            builder.AppendLine($"  <h1>{System.Net.WebUtility.HtmlEncode(DisplayName)}</h1>");
            builder.AppendLine($"  <p><strong>Format:</strong> {System.Net.WebUtility.HtmlEncode(Format)} | <strong>Drift count:</strong> {DriftCount} | <strong>Updated:</strong> {LastUpdatedText}</p>");
            builder.AppendLine("  <h2>Servers</h2>");
            builder.AppendLine("  <table>");
            builder.AppendLine("    <thead><tr><th>Server</th><th>Status</th><th>Drift lines</th><th>Present</th></tr></thead>");
            builder.AppendLine($"    <tbody>{serverRows}</tbody>");
            builder.AppendLine("  </table>");
            builder.AppendLine("  <h2>Notes</h2>");
            builder.AppendLine($"  <ul>{notes}</ul>");
            builder.AppendLine("  <h2>Diff (before)</h2>");
            builder.AppendLine($"  <pre>{System.Net.WebUtility.HtmlEncode(DiffBefore)}</pre>");
            builder.AppendLine("  <h2>Diff (after)</h2>");
            builder.AppendLine($"  <pre>{System.Net.WebUtility.HtmlEncode(DiffAfter)}</pre>");
            builder.AppendLine("</body>");
            builder.AppendLine("</html>");
            return builder.ToString();
        }

        private static string BuildUnifiedDiff(string before, string after)
        {
            if (string.IsNullOrWhiteSpace(before) && string.IsNullOrWhiteSpace(after))
            {
                return string.Empty;
            }

            var beforeLines = (before ?? string.Empty).Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var afterLines = (after ?? string.Empty).Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var lines = new List<string>();
            foreach (var line in beforeLines.Except(afterLines))
            {
                lines.Add($"- {line}");
            }

            foreach (var line in afterLines.Except(beforeLines))
            {
                lines.Add($"+ {line}");
            }

            return string.Join(Environment.NewLine, lines);
        }

        private record DrilldownExportSnapshot
        {
            public required string ConfigId { get; init; }
            public required string DisplayName { get; init; }
            public required string Format { get; init; }
            public required int DriftCount { get; init; }
            public required DateTimeOffset LastUpdated { get; init; }
            public required bool HasSecrets { get; init; }
            public required bool HasMaskedTokens { get; init; }
            public required bool HasValidationIssues { get; init; }
            public required IReadOnlyList<string> Notes { get; init; }
            public required string Provenance { get; init; }
            public required string DiffBefore { get; init; }
            public required string DiffAfter { get; init; }
            public required string UnifiedDiff { get; init; }
            public required List<DrilldownExportServer> Servers { get; init; }
        }

        private record DrilldownExportServer
        {
            public required string HostId { get; init; }
            public required string Label { get; init; }
            public required bool Present { get; init; }
            public required bool IsBaseline { get; init; }
            public required string Status { get; init; }
            public required int DriftLineCount { get; init; }
            public required bool HasSecrets { get; init; }
            public required bool Masked { get; init; }
            public required string RedactionStatus { get; init; }
            public required DateTimeOffset LastSeen { get; init; }
        }

        public enum ExportFormat
        {
            Html,
            Json,
        }
    }

    public sealed record ConfigDrilldownExportRequest(string DisplayName, string ConfigId, ConfigDrilldownViewModel.ExportFormat Format, string Payload);
}
