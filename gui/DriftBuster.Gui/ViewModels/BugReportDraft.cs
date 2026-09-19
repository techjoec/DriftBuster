using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;

using CommunityToolkit.Mvvm.ComponentModel;

namespace DriftBuster.Gui.ViewModels
{
    /// <summary>
    /// A bug report about one file or setting, for the user to read, edit and send. The file path is relative to the scanned
    /// root, so no host name goes out; values that are masked stay masked. <see cref="PayloadText"/> is exactly what is sent: it
    /// is rebuilt when a field changes, and the user can edit it by hand to take anything out.
    /// </summary>
    public sealed partial class BugReportDraft : ObservableObject
    {
        /// <summary>Where GitHub issues are filed.</summary>
        public const string IssuesUrl = "https://github.com/techjoec/DriftBuster/issues/new";

        /// <summary>GitHub rejects very long prefilled links; longer bodies are cut and say so.</summary>
        internal const int MaxUrlLength = 7500;

        // Relaxed escaping keeps the masked marker and non-ASCII values readable; the text is shown and copied, never embedded in HTML.
        private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        public BugReportDraft(CompareContext context)
            : this(
                (context ?? throw new ArgumentNullException(nameof(context))).Path,
                context.Key ?? string.Empty,
                context.File.Format,
                context.File.Mode,
                // A secret goes out as its marker even when the user has it unmasked on screen.
                context.Row?.Cells.ToDictionary(cell => cell.HostLabel, cell => cell.IsSecret ? (cell.IsDifferent ? "•••• (differs)" : "•••• (same)") : cell.Text, StringComparer.Ordinal)
                    ?? new Dictionary<string, string>(StringComparer.Ordinal),
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["summary"] = context.File.Summary,
                    ["file_name"] = context.File.FileName,
                    ["application"] = context.File.AppName,
                    ["groups"] = string.Join(", ", context.Row?.Groups ?? []),
                    ["ignored"] = (context.Row?.Ignored ?? context.File.Ignored) ? "yes" : "no",
                    ["differs"] = (context.Row?.RawDiffers ?? context.File.RawDiffers) ? "yes" : "no",
                    ["value_masked"] = context.Row?.Cells.Any(cell => cell.IsSecret) == true ? "yes" : "no",
                })
        {
        }

        /// <summary>A report about anything with a path: the source path (relative, no host), what in it, and what is known.</summary>
        public BugReportDraft(
            string sourcePath,
            string setting,
            string format,
            string mode,
            IReadOnlyDictionary<string, string> values,
            IReadOnlyDictionary<string, string> metadata,
            string? category = null)
        {
            SourcePath = sourcePath ?? throw new ArgumentNullException(nameof(sourcePath));
            Setting = setting ?? string.Empty;
            Format = format ?? string.Empty;
            Mode = mode ?? string.Empty;
            Values = values ?? throw new ArgumentNullException(nameof(values));
            Metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
            _category = category is not null && Categories.Contains(category, StringComparer.Ordinal) ? category : Categories[0];
            _payloadText = Payload;
        }

        public static IReadOnlyList<string> Categories { get; } =
        [
            "Wrong detection (format or type)",
            "Settings read wrongly (bad parse)",
            "Wrong value type",
            "Not a configuration file",
            "Secret not masked, or masked wrongly",
            "Hunt finding is not a real secret or value (false positive)",
            "Something else",
        ];

        public string SourcePath { get; }

        public string Setting { get; }

        public string Format { get; }

        public string Mode { get; }

        public IReadOnlyDictionary<string, string> Values { get; }

        public IReadOnlyDictionary<string, string> Metadata { get; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(Payload), nameof(Title))]
        private string _category = Categories[0];

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(Payload))]
        private string _description = string.Empty;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(Payload))]
        private string _contactEmail = string.Empty;

        /// <summary>Include each server's value; off leaves values out entirely.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(Payload))]
        private bool _includeValues = true;

        /// <summary>The text that is sent: <see cref="Payload"/> until the user edits it.</summary>
        [ObservableProperty]
        private string _payloadText = string.Empty;

        partial void OnCategoryChanged(string value) => PayloadText = Payload;

        partial void OnDescriptionChanged(string value) => PayloadText = Payload;

        partial void OnContactEmailChanged(string value) => PayloadText = Payload;

        partial void OnIncludeValuesChanged(bool value) => PayloadText = Payload;

        public string Title => Setting.Length > 0 ? $"{Category}: {SourcePath} {Setting}" : $"{Category}: {SourcePath}";

        /// <summary>The whole report as JSON: exactly what is sent.</summary>
        public string Payload => JsonSerializer.Serialize(new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["title"] = Title,
            ["category"] = Category,
            ["source_path"] = SourcePath,
            ["setting"] = Setting,
            ["format"] = Format,
            ["mode"] = Mode,
            ["values"] = IncludeValues ? Values : new Dictionary<string, string>(StringComparer.Ordinal),
            ["metadata"] = Metadata,
            ["description"] = Description,
            ["contact_email"] = ContactEmail,
        }, Options);

        /// <summary>A link that opens a new GitHub issue with the title and the payload filled in.</summary>
        public string GitHubIssueUrl()
        {
            var body = new StringBuilder()
                .Append(Description.Length > 0 ? Description : "(describe what went wrong)")
                .Append("\n\n```json\n").Append(PayloadText).Append("\n```\n")
                .ToString();
            var url = Build(body);
            if (url.Length <= MaxUrlLength)
            {
                return url;
            }

            const string cut = "\n\n(The payload was cut to fit in a link; attach the rest if it matters.)";
            var keep = Math.Max(0, body.Length - (url.Length - MaxUrlLength) - cut.Length - 64);
            while (keep > 0 && Build(body[..keep] + cut).Length > MaxUrlLength)
            {
                keep = Math.Max(0, keep - 256);
            }

            return Build(body[..keep] + cut);
        }

        private string Build(string body) =>
            $"{IssuesUrl}?title={Uri.EscapeDataString(Title)}&body={Uri.EscapeDataString(body)}&labels={Uri.EscapeDataString("bug")}";
    }
}
