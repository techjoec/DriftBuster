using System.Globalization;
using System.Text;

using DriftBuster.Backend.Diff;
using DriftBuster.Backend.Infrastructure;

namespace DriftBuster.Backend.Reporting;

public static partial class HtmlReport
{
    private const string NoValue = "\u2014";

    /// <summary><c>_format_metadata</c>: one table row per item, ordered by key code point.</summary>
    internal static string FormatMetadata(IReadOnlyDictionary<string, object?> metadata)
    {
        var keys = metadata.Keys.ToList();
        EngineSort<string>.Sort(keys, static (left, right) => PathText.CompareCodePoints(left, right) < 0);
        return string.Join('\n', keys.Select(key => $"<tr><th>{ReportValues.Escape(key)}</th><td>{ReportValues.Escape(ReportValues.Str(metadata[key]))}</td></tr>"));
    }

    /// <summary><c>_render_match</c>.</summary>
    internal static string RenderMatch(IReadOnlyDictionary<string, object?> match, int index)
    {
        var metadataTable = ReportValues.IsMapping(match.GetValueOrDefault("metadata"), out var metadata)
            ? $"<table>{FormatMetadata(metadata)}</table>"
            : string.Empty;
        var reasons = match.TryGetValue("reasons", out var rawReasons) ? rawReasons : new List<object?>();
        var reasonsList = string.Concat(ReportValues.Iterate(reasons).Select(reason => $"<li>{ReportValues.Escape(ReportValues.Str(reason))}</li>"));
        var variant = ReportValues.Escape(ReportValues.Str(match.GetValueOrDefault("variant")));
        return string.Create(
            CultureInfo.InvariantCulture,
            $"<section class=\"match\"><h3>Match {index}: {ReportValues.Escape(ReportValues.Str(match.GetValueOrDefault("format")))}</h3>"
            + $"<p><strong>Plugin:</strong> {ReportValues.Escape(ReportValues.Str(match.GetValueOrDefault("plugin")))} | "
            + $"<strong>Variant:</strong> {(variant.Length > 0 ? variant : NoValue)}</p>"
            + $"<p><strong>Confidence:</strong> {ReportValues.Escape(ReportValues.Str(match.GetValueOrDefault("confidence")))}</p>"
            + $"<h4>Reasons</h4><ul>{(reasonsList.Length > 0 ? reasonsList : "<li>None provided</li>")}</ul>"
            + $"<h4>Metadata</h4>{metadataTable}</section>");
    }

    /// <summary>
    /// <c>_render_detection_summary</c>: one row per (format, variant) in tuple order, with the match count and the peak confidence as
    /// <c>{:.2f}</c> (a confidence <c>float()</c> rejects counts as 0.0).
    /// </summary>
    internal static string RenderDetectionSummary(IReadOnlyList<IReadOnlyDictionary<string, object?>> matches)
    {
        if (matches.Count == 0)
        {
            return string.Empty;
        }

        var aggregates = new Dictionary<(string Format, string Variant), (int Count, double MaxConfidence)>();
        foreach (var record in matches)
        {
            var format = ReportValues.Str(FirstTruthy(record.GetValueOrDefault("format"), "unknown"));
            var variant = ReportValues.Str(FirstTruthy(record.GetValueOrDefault("variant"), NoValue));
            var confidence = ReportValues.FloatOrZero(record.TryGetValue("confidence", out var raw) ? raw : 0.0);
            var bucket = aggregates.TryGetValue((format, variant), out var existing) ? existing : (Count: 0, MaxConfidence: 0.0);
            aggregates[(format, variant)] = (bucket.Count + 1, ReportValues.Max(bucket.MaxConfidence, confidence));
        }

        var keys = aggregates.Keys.ToList();
        EngineSort<(string Format, string Variant)>.Sort(keys, static (left, right) =>
        {
            var byFormat = PathText.CompareCodePoints(left.Format, right.Format);
            return byFormat != 0 ? byFormat < 0 : PathText.CompareCodePoints(left.Variant, right.Variant) < 0;
        });
        var rows = new StringBuilder();
        foreach (var key in keys)
        {
            var info = aggregates[key];
            rows.Append(CultureInfo.InvariantCulture, $"<tr><td>{ReportValues.Escape(key.Format)}</td><td>{ReportValues.Escape(key.Variant)}</td>")
                .Append(CultureInfo.InvariantCulture, $"<td>{info.Count}</td><td>{ReportValues.FormatFixed(info.MaxConfidence, 2)}</td></tr>");
        }

        return "<section class=\"match summary\"><h2>Detection Summary</h2><table class=\"summary-table\">"
            + "<thead><tr><th>Format</th><th>Variant</th><th>Matches</th><th>Peak confidence</th></tr></thead>"
            + $"<tbody>{rows}</tbody></table></section>";
    }

    private static object? FirstTruthy(object? value, object fallback) => ReportValues.Truthy(value) ? value : fallback;

    /// <summary>
    /// <c>_serialise_diff</c>: a <see cref="DiffArtifact"/> gives its label (or <c>Diff</c>), diff, stats and, when present, safety limits;
    /// anything else is <c>dict(diff)</c> (<see cref="EngineBuiltins.Dict"/>): a mapping copied, a sequence read as key/value pairs, with
    /// Python's errors.
    /// </summary>
    internal static OrderedDictionary<string, object?> SerialiseDiff(object diff)
    {
        if (diff is DiffArtifact artifact)
        {
            var payload = new OrderedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["label"] = string.IsNullOrEmpty(artifact.Label) ? "Diff" : artifact.Label,
                ["diff"] = artifact.Diff,
                ["stats"] = new OrderedDictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["added_lines"] = artifact.Stats.AddedLines,
                    ["removed_lines"] = artifact.Stats.RemovedLines,
                    ["changed_lines"] = artifact.Stats.ChangedLines,
                },
            };
            if (artifact.SafetyLimits is not null)
            {
                payload["safety_limits"] = artifact.SafetyLimits.ToPayload();
            }

            return payload;
        }

        return ReportValues.IsMapping(diff, out var mapping) ? ReportValues.Copy(mapping) : EngineBuiltins.Dict(diff, "diff");
    }

    /// <summary><c>_render_diff_section</c>: an article per diff with its label, non-empty stats, safety notice and escaped diff text.</summary>
    internal static string RenderDiffSection(IReadOnlyList<IReadOnlyDictionary<string, object?>> diffs)
    {
        if (diffs.Count == 0)
        {
            return string.Empty;
        }

        var sections = new StringBuilder("<section class=\"diffs\"><h2>Configuration Diffs</h2>");
        foreach (var entry in diffs)
        {
            var label = ReportValues.Escape(ReportValues.Str(FirstTruthy(entry.GetValueOrDefault("label"), "Diff")));
            var diffText = ReportValues.Escape(ReportValues.Str(FirstTruthy(entry.GetValueOrDefault("diff"), string.Empty)));
            sections.Append("<article class=\"diff-block\">").Append("<h3>").Append(label).Append("</h3>");
            if (ReportValues.IsMapping(entry.GetValueOrDefault("stats"), out var stats) && stats.Count > 0)
            {
                sections.Append("<ul class=\"diff-stats\">");
                foreach (var (key, value) in stats)
                {
                    sections.Append("<li>").Append(ReportValues.Escape(key)).Append(": ").Append(ReportValues.Escape(ReportValues.Str(value))).Append("</li>");
                }

                sections.Append("</ul>");
            }

            var noticeText = FormatSafetyNotice(entry.GetValueOrDefault("safety_limits"));
            if (noticeText.Length > 0)
            {
                sections.Append("<p class=\"diff-safety\">").Append(ReportValues.Escape(noticeText)).Append("</p>");
            }

            sections.Append("<pre>").Append(diffText).Append("</pre></article>");
        }

        return sections.Append("</section>").ToString();
    }

    /// <summary>
    /// <c>_serialise_hunt_hit</c> (the HTML variant): a mapping is copied; a hit gives its rule with <c>keywords</c> and the tuple of its
    /// pattern sources under <c>patterns</c>, its path, line number and excerpt.
    /// </summary>
    internal static OrderedDictionary<string, object?> SerialiseHuntHit(object hit)
    {
        if (ReportValues.IsMapping(hit, out var mapping))
        {
            return ReportValues.Copy(mapping);
        }

        var finding = JsonLinesReport.AsFinding(hit);
        return new OrderedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["rule"] = new OrderedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["name"] = finding.Rule.Name,
                ["description"] = finding.Rule.Description,
                ["token_name"] = finding.Rule.TokenName,
                ["keywords"] = finding.Rule.Keywords.Cast<object?>().ToArray(),
                ["patterns"] = finding.Rule.Patterns.Select(pattern => (object?)pattern.Pattern).ToArray(),
            },
            ["path"] = finding.Path,
            ["line_number"] = finding.LineNumber,
            ["excerpt"] = finding.Excerpt,
        };
    }

    /// <summary><c>_render_hunt_section</c>.</summary>
    internal static string RenderHuntSection(IReadOnlyList<IReadOnlyDictionary<string, object?>> hits)
    {
        if (hits.Count == 0)
        {
            return string.Empty;
        }

        var items = new StringBuilder();
        foreach (var entry in hits)
        {
            var rule = entry.TryGetValue("rule", out var rawRule) ? rawRule : new OrderedDictionary<string, object?>(StringComparer.Ordinal);
            var isMapping = ReportValues.IsMapping(rule, out var ruleMap);
            var tokenName = isMapping ? ruleMap.GetValueOrDefault("token_name") : null;
            var tokenBadge = ReportValues.Truthy(tokenName)
                ? $"<span class=\"badge\">token: {ReportValues.Escape(ReportValues.Str(tokenName))}</span>"
                : string.Empty;
            var description = isMapping ? ruleMap.GetValueOrDefault("description") : string.Empty;
            items.Append("<li><strong>").Append(ReportValues.Escape(ReportValues.Str(entry.GetValueOrDefault("path"))))
                .Append("</strong> \u2014 line ").Append(ReportValues.Escape(ReportValues.Str(entry.GetValueOrDefault("line_number"))))
                .Append("<br/><em>").Append(ReportValues.Escape(ReportValues.Str(description))).Append("</em> ").Append(tokenBadge)
                .Append("<br/><code>").Append(ReportValues.Escape(ReportValues.Str(entry.GetValueOrDefault("excerpt")))).Append("</code></li>");
        }

        return "<section class=\"hunt-section\"><h2>Hunt Highlights</h2><ul>" + items + "</ul></section>";
    }

    /// <summary><c>_render_profile_summary</c>: the totals present, then one row per mapping profile (its config count unescaped).</summary>
    internal static string RenderProfileSummary(IReadOnlyDictionary<string, object?> summary)
    {
        if (summary.Count == 0)
        {
            return string.Empty;
        }

        var totals = new StringBuilder();
        foreach (var (key, heading) in new[] { ("total_profiles", "Total Profiles"), ("total_configs", "Total Configs"), ("total_tags", "Total Tags") })
        {
            if (summary.TryGetValue(key, out var total))
            {
                totals.Append("<li>").Append(heading).Append(": ").Append(ReportValues.Escape(ReportValues.Str(total))).Append("</li>");
            }
        }

        var profileRows = new StringBuilder();
        var profiles = summary.TryGetValue("profiles", out var rawProfiles) ? rawProfiles : new List<object?>();
        foreach (var profile in ReportValues.Iterate(profiles))
        {
            if (!ReportValues.IsMapping(profile, out var profileMap))
            {
                continue;
            }

            var configIdValues = profileMap.TryGetValue("config_ids", out var rawIds) ? rawIds : new List<object?>();
            var configIds = string.Join(", ", ReportValues.Iterate(configIdValues).Select(ReportValues.Str));
            profileRows.Append("<tr><td>").Append(ReportValues.Escape(ReportValues.Str(profileMap.GetValueOrDefault("name"))))
                .Append("</td><td>").Append(ReportValues.Str(profileMap.TryGetValue("config_count", out var count) ? count : 0))
                .Append("</td><td>").Append(ReportValues.Escape(configIds.Length > 0 ? configIds : NoValue)).Append("</td></tr>");
        }

        var parts = new StringBuilder("<section class=\"profile-summary\"><h2>Profile Summary</h2>");
        if (totals.Length > 0)
        {
            parts.Append("<ul>").Append(totals).Append("</ul>");
        }

        if (profileRows.Length > 0)
        {
            parts.Append("<table><thead><tr><th>Name</th><th>Configs</th><th>Config IDs</th></tr></thead><tbody>").Append(profileRows).Append("</tbody></table>");
        }

        return parts.Append("</section>").ToString();
    }
}
