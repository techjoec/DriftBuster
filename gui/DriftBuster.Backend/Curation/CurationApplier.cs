using DriftBuster.Backend.Models;
using DriftBuster.Backend.Settings;

namespace DriftBuster.Backend.Curation;

/// <summary>
/// Applies the user's curation to a comparison: rule labels, groups, the review list, ignores (a source, a setting, or one value)
/// and mask or unmask choices, then recounts what differs. The source comparison is left untouched so curation can be applied
/// again after every change. Choices apply in order rules, saved choices, then this run's choices, so for mask and unmask the
/// latest word wins.
/// </summary>
public static class CurationApplier
{
    public static SettingsComparison Apply(
        SettingsComparison source,
        CurationDocument document,
        IReadOnlyList<CurationChoice> session,
        string hostSetId)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(hostSetId);

        var rules = document.Rules.Where(rule => rule.Enabled && CurationScopes.Applies(rule.Scope, hostSetId)).ToList();
        var groups = document.Groups.Where(group => CurationScopes.Applies(group.Scope, hostSetId)).ToList();
        var choices = document.Choices.Where(choice => CurationScopes.Applies(choice.Scope, hostSetId)).Concat(session).ToList();

        var result = Clone(source);
        foreach (var file in result.Files)
        {
            ApplyToFile(file, rules, groups, choices, document.Review);
        }

        SettingsTally.Recount(result);
        return result;
    }

    private static void ApplyToFile(
        FileComparison file,
        List<CurationRule> rules,
        List<CurationGroup> groups,
        List<CurationChoice> choices,
        IReadOnlyList<CurationReviewItem> review)
    {
        var fileRules = rules.Where(rule => rule.KeyPattern.Length == 0 && rule.MatchesFile(file.Path)).ToList();
        file.AppName = fileRules.Select(rule => rule.AppName).FirstOrDefault(name => name.Length > 0) ?? string.Empty;
        file.FileLabel = fileRules.Select(rule => rule.FileLabel).FirstOrDefault(name => name.Length > 0) ?? string.Empty;
        file.Description = fileRules.Select(rule => rule.Description).FirstOrDefault(text => text.Length > 0) ?? string.Empty;
        file.Ignored = fileRules.Any(rule => rule.Ignore)
            || choices.Any(choice => string.Equals(choice.Kind, CurationChoiceKinds.Ignore, StringComparison.Ordinal) && choice.Target.IsSource && choice.Target.MatchesFile(file.Path));

        foreach (var row in file.Settings)
        {
            var rowRules = rules.Where(rule => rule.KeyPattern.Length == 0 ? rule.MatchesFile(file.Path) : rule.MatchesSetting(file.Path, row.Key)).ToList();
            row.Groups = groups
                .Where(group => group.Members.Any(member => member.IsSource ? member.MatchesFile(file.Path) : member.MatchesSetting(file.Path, row.Key)))
                .Select(group => group.Name)
                .Concat(rowRules.SelectMany(rule => rule.Groups))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            row.Ignored = rowRules.Any(rule => rule.Ignore && rule.KeyPattern.Length > 0)
                || choices.Any(choice => string.Equals(choice.Kind, CurationChoiceKinds.Ignore, StringComparison.Ordinal) && !choice.Target.IsSource && !choice.Target.IsValue
                    && choice.Target.MatchesSetting(file.Path, row.Key));
            row.InReview = review.Any(item => item.Target.MatchesSetting(file.Path, row.Key));

            foreach (var value in row.Values)
            {
                value.Ignored = choices.Any(choice => string.Equals(choice.Kind, CurationChoiceKinds.Ignore, StringComparison.Ordinal) && choice.Target.IsValue
                    && choice.Target.MatchesValue(file.Path, row.Key, value.ValueHash));
            }

            ApplyMask(row, MaskWord(file.Path, row.Key, rowRules, choices));
        }
    }

    // The last mask or unmask that applies: rules first, then choices in the order they were made.
    private static string MaskWord(string path, string key, List<CurationRule> rowRules, List<CurationChoice> choices)
    {
        var word = rowRules.Select(rule => rule.Mask).LastOrDefault(mask => mask.Length > 0) ?? string.Empty;
        foreach (var choice in choices)
        {
            if (choice.Kind is CurationChoiceKinds.Mask or CurationChoiceKinds.Unmask
                && (choice.Target.IsSource ? choice.Target.MatchesFile(path) : choice.Target.MatchesSetting(path, key)))
            {
                word = choice.Kind;
            }
        }

        return word;
    }

    private static void ApplyMask(SettingRow row, string word)
    {
        foreach (var value in row.Values.Where(value => value.State == SettingValueState.Value))
        {
            if (string.Equals(word, CurationChoiceKinds.Unmask, StringComparison.Ordinal) && value.Masked && value.SecretValue is not null)
            {
                value.Value = value.SecretValue;
                value.Masked = false;
            }
            else if (string.Equals(word, CurationChoiceKinds.Mask, StringComparison.Ordinal) && !value.Masked && value.Value is not null)
            {
                value.SecretValue = value.Value;
                value.Value = null;
                value.Masked = true;
            }
        }
    }

    private static SettingsComparison Clone(SettingsComparison source) => new()
    {
        BaselineHostId = source.BaselineHostId,
        Hosts = source.Hosts.Select(host => new HostComparisonSummary
        {
            HostId = host.HostId,
            Label = host.Label,
            IsBaseline = host.IsBaseline,
            Scanned = host.Scanned,
            ScanMessage = host.ScanMessage,
            SettingsDiffering = host.SettingsDiffering,
            FilesDiffering = host.FilesDiffering,
            FilesMissing = host.FilesMissing,
            FilesExtra = host.FilesExtra,
            FilesUnreadable = host.FilesUnreadable,
            MatchesBaseline = host.MatchesBaseline,
        }).ToArray(),
        Files = source.Files.Select(file => new FileComparison
        {
            ConfigId = file.ConfigId,
            Path = file.Path,
            Format = file.Format,
            Mode = file.Mode,
            SettingsDiffering = file.SettingsDiffering,
            Differs = file.Differs,
            Presence = file.Presence.Select(CloneValue).ToArray(),
            Settings = file.Settings.Select(row => new SettingRow { Key = row.Key, Differs = row.Differs, Values = row.Values.Select(CloneValue).ToArray() }).ToArray(),
        }).ToArray(),
    };

    private static SettingValue CloneValue(SettingValue value) => new()
    {
        HostId = value.HostId,
        State = value.State,
        Value = value.Value,
        Masked = value.Masked,
        DiffersFromBaseline = value.DiffersFromBaseline,
        ValueHash = value.ValueHash,
        SecretValue = value.SecretValue,
    };
}
