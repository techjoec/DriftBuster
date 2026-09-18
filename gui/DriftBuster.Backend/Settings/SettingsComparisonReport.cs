using System.Globalization;
using System.Net;
using System.Text;

using DriftBuster.Backend.Models;

namespace DriftBuster.Backend.Settings;

/// <summary>
/// The settings comparison as a report people can open or share: an HTML page (per-server summary, then one table per file that
/// differs, only the settings that differ) and a CSV of the same rows. Masked values stay masked.
/// </summary>
public static class SettingsComparisonReport
{
    public static string Html(SettingsComparison comparison, DateTimeOffset generatedAt)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        var labels = Labels(comparison);
        var builder = new StringBuilder();
        builder.Append("<!DOCTYPE html>\n<html lang=\"en\"><head><meta charset=\"utf-8\"><title>DriftBuster settings comparison</title>\n")
            .Append("<style>body{font-family:Segoe UI,sans-serif;margin:24px;color:#0f172a}table{border-collapse:collapse;width:100%;margin:8px 0 24px}")
            .Append("th,td{border:1px solid #cbd5e1;padding:6px 8px;text-align:left;vertical-align:top;word-break:break-word}th{background:#f1f5f9}")
            .Append(".d{background:#fef3c7;font-weight:600}.m{color:#b91c1c;font-style:italic}.ok{color:#15803d}h2{margin-bottom:4px}.p{color:#475569}</style></head><body>\n")
            .Append("<h1>Settings comparison</h1>\n<p class=\"p\">Generated ")
            .Append(Encode(generatedAt.ToString("yyyy-MM-dd HH:mm 'UTC'zzz", CultureInfo.InvariantCulture)))
            .Append(". Values are compared with ").Append(Encode(Label(labels, comparison.BaselineHostId))).Append(".</p>\n<ul>\n");
        foreach (var host in comparison.Hosts)
        {
            builder.Append("<li><b>").Append(Encode(host.Label)).Append("</b>: ").Append(Encode(HostSummaryText(host))).Append("</li>\n");
        }

        builder.Append("</ul>\n");
        foreach (var file in comparison.Files.Where(file => file.Differs))
        {
            builder.Append("<h2>").Append(Encode(file.Path)).Append("</h2>\n<p class=\"p\">").Append(Encode(FileSummaryText(file, labels))).Append("</p>\n");
            var rows = file.Settings.Where(row => row.Differs).ToList();
            if (rows.Count == 0)
            {
                continue;
            }

            builder.Append("<table><tr><th>Setting</th>");
            foreach (var host in comparison.Hosts)
            {
                builder.Append("<th>").Append(Encode(host.Label)).Append("</th>");
            }

            builder.Append("</tr>\n");
            foreach (var row in rows)
            {
                builder.Append("<tr><td>").Append(Encode(row.Key)).Append("</td>");
                foreach (var value in row.Values)
                {
                    var css = value.DiffersFromBaseline ? " class=\"d\"" : value.State == SettingValueState.Value ? string.Empty : " class=\"m\"";
                    builder.Append("<td").Append(css).Append('>').Append(Encode(CellText(value))).Append("</td>");
                }

                builder.Append("</tr>\n");
            }

            builder.Append("</table>\n");
        }

        return builder.Append("</body></html>\n").ToString();
    }

    public static string Csv(SettingsComparison comparison)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        var builder = new StringBuilder();
        AppendCsvRow(builder, ["File", "Setting", .. comparison.Hosts.Select(host => host.Label)]);
        foreach (var file in comparison.Files.Where(file => file.Differs))
        {
            if (file.Presence.Any(value => value.DiffersFromBaseline))
            {
                AppendCsvRow(builder, [file.Path, "(file)", .. file.Presence.Select(PresenceText)]);
            }

            foreach (var row in file.Settings.Where(row => row.Differs))
            {
                AppendCsvRow(builder, [file.Path, row.Key, .. row.Values.Select(CellText)]);
            }
        }

        return builder.ToString();
    }

    /// <summary>A server's standing in words: "matches the baseline", "3 settings differ in 2 files, 1 file missing", ...</summary>
    public static string HostSummaryText(HostComparisonSummary host)
    {
        ArgumentNullException.ThrowIfNull(host);
        if (host.IsBaseline)
        {
            return "baseline (the others are compared with it)";
        }

        if (!host.Scanned)
        {
            return string.IsNullOrWhiteSpace(host.ScanMessage) ? "not scanned" : $"not scanned: {host.ScanMessage}";
        }

        if (host.MatchesBaseline)
        {
            return "matches the baseline";
        }

        var parts = new List<string>();
        if (host.SettingsDiffering > 0)
        {
            parts.Add($"{Count(host.SettingsDiffering, "setting")} {(host.SettingsDiffering == 1 ? "differs" : "differ")} in {Count(Math.Max(host.FilesDiffering - host.FilesMissing.Length - host.FilesExtra.Length - host.FilesUnreadable.Length, 1), "file")}");
        }

        AddFiles(parts, host.FilesMissing.Length, "missing");
        AddFiles(parts, host.FilesExtra.Length, "only here");
        AddFiles(parts, host.FilesUnreadable.Length, "unreadable");
        return parts.Count == 0 ? "differs from the baseline" : string.Join(", ", parts);
    }

    /// <summary>A file's standing in words: "2 settings differ", "missing on prod", "only on prod", ...</summary>
    public static string FileSummaryText(FileComparison file, IReadOnlyDictionary<string, string> labels)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(labels);
        var parts = new List<string>();
        if (file.SettingsDiffering > 0)
        {
            parts.Add($"{Count(file.SettingsDiffering, "setting")} {(file.SettingsDiffering == 1 ? "differs" : "differ")}");
        }

        var baselineHas = file.Presence.Any(value => !value.DiffersFromBaseline && value.State == SettingValueState.Value);
        AddHosts(parts, file, labels, SettingValueState.FileMissing, "missing on");
        AddHosts(parts, file, labels, SettingValueState.Value, baselineHas ? "present on" : "only on");
        AddHosts(parts, file, labels, SettingValueState.Unreadable, "unreadable on");
        if (parts.Count == 0)
        {
            parts.Add("same on every server");
        }

        if (string.Equals(file.Mode, "lines", StringComparison.Ordinal))
        {
            parts.Add("compared line by line");
        }

        return string.Join(" · ", parts);
    }

    /// <summary>What a cell shows: the value, a masked marker, or the reason there is no value.</summary>
    public static string CellText(SettingValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.State switch
        {
            SettingValueState.Value when value.Masked => value.DiffersFromBaseline ? "•••• (differs)" : "•••• (same)",
            SettingValueState.Value => value.Value ?? string.Empty,
            SettingValueState.NotSet => "not set",
            SettingValueState.FileMissing => "file missing",
            SettingValueState.Unreadable => "unreadable",
            _ => "not scanned",
        };
    }

    public static IReadOnlyDictionary<string, string> Labels(SettingsComparison comparison)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        return comparison.Hosts.GroupBy(host => host.HostId, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Last().Label, StringComparer.Ordinal);
    }

    private static string PresenceText(SettingValue value) => value.State == SettingValueState.Value ? "present" : CellText(value);

    private static void AddFiles(List<string> parts, int count, string what)
    {
        if (count > 0)
        {
            parts.Add($"{Count(count, "file")} {what}");
        }
    }

    private static void AddHosts(List<string> parts, FileComparison file, IReadOnlyDictionary<string, string> labels, SettingValueState state, string what)
    {
        var hosts = file.Presence.Where(value => value.DiffersFromBaseline && value.State == state).Select(value => Label(labels, value.HostId)).ToList();
        if (hosts.Count > 0)
        {
            parts.Add($"{what} {string.Join(", ", hosts)}");
        }
    }

    private static string Label(IReadOnlyDictionary<string, string> labels, string hostId) => labels.TryGetValue(hostId, out var label) ? label : hostId;

    private static string Count(int count, string noun) => string.Create(CultureInfo.InvariantCulture, $"{count} {noun}{(count == 1 ? string.Empty : "s")}");

    private static string Encode(string text) => WebUtility.HtmlEncode(text);

    private static void AppendCsvRow(StringBuilder builder, IEnumerable<string> cells)
    {
        builder.AppendJoin(',', cells.Select(cell => cell.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + cell.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : cell));
        builder.Append("\r\n");
    }
}
