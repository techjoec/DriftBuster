using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using DriftBuster.Backend;
using DriftBuster.Backend.Curation;
using DriftBuster.Backend.History;
using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Settings;

namespace DriftBuster.Gui.ViewModels
{
    /// <summary>
    /// What the right-click menu does: groups, rules, marks, ignore and mask choices, the review list, copies, history and bug
    /// reports. Choices that last go through <see cref="Curation"/>; choices for this run stay here until the app closes.
    /// </summary>
    public sealed partial class CompareViewModel
    {
        private readonly List<CurationChoice> _sessionChoices = new();
        private readonly HashSet<string> _marks = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The raw text of one server's copy of a file (config id, host id), when the owner can provide it.</summary>
        public Func<string, string, string?>? RawTextProvider { get; set; }

        public IReadOnlyList<string> GroupNames => Curation.Document.Groups.Select(group => group.Name).ToArray();

        public IReadOnlyList<string> RuleNames => Curation.Document.Rules.Select(rule => rule.Name).ToArray();

        private static string MarkKey(string path, string key) => path + "\n" + key;

        private static CurationTarget TargetOf(CompareContext context, CompareTargetLevel level) => level switch
        {
            CompareTargetLevel.Source => new CurationTarget { File = context.Path },
            CompareTargetLevel.Value => new CurationTarget { File = context.Path, Key = context.Key ?? string.Empty, ValueHash = context.Cell?.ValueHash ?? string.Empty },
            _ => new CurationTarget { File = context.Path, Key = context.Key ?? string.Empty },
        };

        private string ScopeOf(CurationPersistence persistence) =>
            persistence == CurationPersistence.TheseServers ? HostSetId : CurationScopes.AllRuns;

        private void Say(string message) => StatusMessage = message;

        // Groups

        /// <summary>The groups the right-clicked setting (or file) belongs to.</summary>
        public IReadOnlyList<string> GroupsOf(CompareContext context) =>
            context.Row?.Groups ?? Curation.Document.Groups
                .Where(group => group.Members.Any(member => member.IsSource && member.MatchesFile(context.Path)))
                .Select(group => group.Name)
                .ToArray();

        public void AddToGroup(CompareContext context, string groupName)
        {
            ArgumentNullException.ThrowIfNull(context);
            var name = groupName?.Trim() ?? string.Empty;
            if (name.Length == 0)
            {
                return;
            }

            var target = TargetOf(context, context.Row is null ? CompareTargetLevel.Source : CompareTargetLevel.Setting);
            Curation.Update(document =>
            {
                var groups = document.Groups.ToList();
                var index = groups.FindIndex(group => string.Equals(group.Name, name, StringComparison.OrdinalIgnoreCase));
                if (index < 0)
                {
                    groups.Add(new CurationGroup { Name = name, Members = [target] });
                }
                else if (!groups[index].Members.Contains(target))
                {
                    groups[index] = groups[index] with { Members = groups[index].Members.Append(target).ToArray() };
                }

                return document with { Groups = groups };
            });
            Say($"Added {context.Key ?? context.Path} to group \"{name}\".");
        }

        public void RemoveFromGroup(CompareContext context, string groupName)
        {
            ArgumentNullException.ThrowIfNull(context);
            var target = TargetOf(context, context.Row is null ? CompareTargetLevel.Source : CompareTargetLevel.Setting);
            var group = Curation.Document.Groups.FirstOrDefault(existing => string.Equals(existing.Name, groupName, StringComparison.OrdinalIgnoreCase));
            if (group is null || !group.Members.Contains(target))
            {
                // Membership through a pattern or a rule is changed where it is defined.
                Say($"{context.Key ?? context.Path} is in \"{groupName}\" through a pattern or rule; change it in Manage groups or Manage rules.");
                return;
            }

            Curation.Update(document => document with
            {
                Groups = document.Groups.Select(existing => ReferenceEquals(existing, group) ? existing with { Members = existing.Members.Where(member => member != target).ToArray() } : existing).ToArray(),
            });
            Say($"Removed {context.Key ?? context.Path} from group \"{groupName}\".");
        }

        /// <summary>Shows only the group's settings, differing or not.</summary>
        public void ViewGroup(string groupName)
        {
            DifferencesOnly = false;
            GroupFilter = groupName;
        }

        public void SaveGroups(IReadOnlyList<CurationGroup> groups)
        {
            Curation.Update(document => document with { Groups = groups.ToArray() });
            if (GroupFilter is not null && groups.All(group => !string.Equals(group.Name, GroupFilter, StringComparison.OrdinalIgnoreCase)))
            {
                GroupFilter = null;
            }
        }

        // Rules

        /// <summary>A new rule for the right-clicked setting or file, for the user to adjust before saving.</summary>
        public CurationRule RuleDraftFor(CompareContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            return new CurationRule
            {
                Name = context.Key is null ? context.File.FileName : $"{context.File.FileName} {context.Key}",
                FilePattern = context.Path,
                KeyPattern = context.Key ?? string.Empty,
                AppName = context.File.AppName,
            };
        }

        /// <summary>Widens an existing rule to cover the right-clicked setting (or file).</summary>
        public void AddToRule(CompareContext context, string ruleName)
        {
            ArgumentNullException.ThrowIfNull(context);
            Curation.Update(document => document with
            {
                Rules = document.Rules.Select(rule =>
                {
                    if (!string.Equals(rule.Name, ruleName, StringComparison.OrdinalIgnoreCase))
                    {
                        return rule;
                    }

                    return context.Key is not null && rule.KeyPattern.Length > 0
                        ? rule with { KeyPattern = Append(rule.KeyPattern, context.Key), FilePattern = rule.MatchesFile(context.Path) ? rule.FilePattern : Append(rule.FilePattern, context.Path) }
                        : rule with { FilePattern = Append(rule.FilePattern, context.Path) };
                }).ToArray(),
            });
            Say($"Rule \"{ruleName}\" now covers {context.Key ?? context.Path}.");

            static string Append(string patterns, string addition) =>
                patterns.Length == 0 || patterns.Split(';', StringSplitOptions.TrimEntries).Contains(addition, StringComparer.OrdinalIgnoreCase)
                    ? (patterns.Length == 0 ? string.Empty : patterns)
                    : $"{patterns};{addition}";
        }

        public void SaveRules(IReadOnlyList<CurationRule> rules) => Curation.Update(document => document with { Rules = rules.ToArray() });

        // Marks

        public void ToggleMark(CompareContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            var rows = context.Row is { } row ? new[] { row } : context.File.AllRows;
            var mark = rows.Any(candidate => !candidate.IsMarked);
            foreach (var candidate in rows)
            {
                candidate.IsMarked = mark;
                if (mark)
                {
                    _marks.Add(MarkKey(candidate.Path, candidate.Key));
                }
                else
                {
                    _marks.Remove(MarkKey(candidate.Path, candidate.Key));
                }
            }

            if (MarkFilter != CompareMarkFilter.All)
            {
                ApplyFilters();
            }
            else
            {
                context.File.RefreshRows();
            }
        }

        public void ClearMarks()
        {
            _marks.Clear();
            foreach (var row in _files.SelectMany(file => file.AllRows))
            {
                row.IsMarked = false;
            }

            ApplyFilters();
        }

        // Ignore and mask

        public void Ignore(CompareContext context, CompareTargetLevel level, CurationPersistence persistence) =>
            Choose(context, level, CurationChoiceKinds.Ignore, persistence);

        /// <summary>Masks (or unmasks) the right-clicked setting's values.</summary>
        public void SetMasked(CompareContext context, bool masked, CurationPersistence persistence) =>
            Choose(context, context.Row is null ? CompareTargetLevel.Source : CompareTargetLevel.Setting, masked ? CurationChoiceKinds.Mask : CurationChoiceKinds.Unmask, persistence);

        private void Choose(CompareContext context, CompareTargetLevel level, string kind, CurationPersistence persistence)
        {
            ArgumentNullException.ThrowIfNull(context);
            // A value choice keeps the value in words for Manage choices; a masked value is never written down.
            var note = level == CompareTargetLevel.Value ? context.Cell?.Value is { } shown ? $"\"{shown}\"" : "a masked value" : string.Empty;
            var choice = new CurationChoice { Target = TargetOf(context, level), Kind = kind, Scope = ScopeOf(persistence), Note = note, CreatedAt = DateTimeOffset.UtcNow };
            if (persistence == CurationPersistence.ThisRun)
            {
                _sessionChoices.Add(choice);
                Refresh();
            }
            else
            {
                Curation.Update(document => document with { Choices = document.Choices.Where(existing => existing.Target != choice.Target || !string.Equals(existing.Scope, choice.Scope, StringComparison.Ordinal) || !SameFamily(existing.Kind, kind)).Append(choice).ToArray() });
            }

            var what = level switch { CompareTargetLevel.Source => context.Path, CompareTargetLevel.Value => $"this value of {context.Key}", _ => context.Key ?? context.Path };
            var when = persistence switch { CurationPersistence.ThisRun => "until the app closes", CurationPersistence.TheseServers => "whenever these servers are compared", _ => "in every run" };
            var verb = kind switch { CurationChoiceKinds.Mask => "Masking", CurationChoiceKinds.Unmask => "Showing", _ => "Ignoring" };
            Say($"{verb} {what} {when}.");

            // A new mask choice replaces an older mask or unmask of the same target; ignores stand alone.
            static bool SameFamily(string a, string b) =>
                string.Equals(a, b, StringComparison.Ordinal)
                || (a is CurationChoiceKinds.Mask or CurationChoiceKinds.Unmask && b is CurationChoiceKinds.Mask or CurationChoiceKinds.Unmask);
        }

        /// <summary>Forgets this run's ignore and mask choices.</summary>
        public void ClearRunChoices()
        {
            _sessionChoices.Clear();
            Refresh();
            Say("Forgot this run's ignore and mask choices.");
        }

        public void SaveChoices(IReadOnlyList<CurationChoice> choices) => Curation.Update(document => document with { Choices = choices.ToArray() });

        // Review list

        public bool IsInReview(CompareContext context) => context.Row?.InReview == true;

        public void ToggleReview(CompareContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            var target = TargetOf(context, context.Row is null ? CompareTargetLevel.Source : CompareTargetLevel.Setting);
            var present = Curation.Document.Review.Any(item => item.Target == target);
            Curation.Update(document => document with
            {
                Review = present
                    ? document.Review.Where(item => item.Target != target).ToArray()
                    : document.Review.Append(new CurationReviewItem { Target = target, AddedAt = DateTimeOffset.UtcNow }).ToArray(),
            });
            Say(present ? $"Removed {context.Key ?? context.Path} from the review list." : $"Added {context.Key ?? context.Path} to the review list.");
        }

        private async Task ExportReviewAsync()
        {
            if (_comparison is null)
            {
                return;
            }

            try
            {
                var directory = DriftbusterPaths.GetExportDirectory();
                var stamp = DateTimeOffset.UtcNow;
                var name = $"settings-review-{stamp:yyyyMMddHHmmss}";
                var html = Path.Combine(directory, name + ".html");
                var csv = Path.Combine(directory, name + ".csv");
                await File.WriteAllTextAsync(html, SettingsComparisonReport.ReviewHtml(_comparison, stamp)).ConfigureAwait(true);
                await File.WriteAllTextAsync(csv, SettingsComparisonReport.ReviewCsv(_comparison)).ConfigureAwait(true);
                Say($"Saved {html} and {Path.GetFileName(csv)}.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Say($"Could not save the review: {ErrorText.Plain(ex)}");
            }
        }

        // Copy, views, history, questions and bug reports

        public static string Copy(CompareContext context, CompareCopyFormat format, CompareCopyScope scope) => CompareCopy.Format(context, format, scope);

        public IReadOnlyList<SettingsTreeNode> TreeOf(CompareFileViewModel file) => SettingsTreeNode.Build(file.AllRows);

        /// <summary>The right-clicked server's copy of the file as text, or null when it is not at hand.</summary>
        public string? RawText(CompareContext context, string hostId)
        {
            ArgumentNullException.ThrowIfNull(context);
            return context.File.HasDetails ? RawTextProvider?.Invoke(context.File.ConfigId, hostId) : null;
        }

        /// <summary>History for the right-clicked setting and value: how the setting changed, where else it is set, where else the value appears.</summary>
        public CompareHistory History(CompareContext context, CompareHistoryKind kind)
        {
            ArgumentNullException.ThrowIfNull(context);
            var key = context.Key ?? string.Empty;
            var hash = context.Cell?.ValueHash ?? context.Row?.Cells.FirstOrDefault(cell => cell.ValueHash is not null)?.ValueHash;
            var wantSetting = kind is CompareHistoryKind.Setting or CompareHistoryKind.Both && key.Length > 0;
            var wantValue = kind is CompareHistoryKind.Value or CompareHistoryKind.Both && hash is not null;
            return new CompareHistory(
                wantSetting ? Curation.SettingHistory(context.Path, key) : Array.Empty<HistoryEntry>(),
                wantSetting ? Curation.WhereSettingIsSet(key) : Array.Empty<HistoryEntry>(),
                wantValue ? Curation.WhereValueAppears(hash!) : Array.Empty<HistoryEntry>());
        }

        /// <summary>The question an assistant would be asked, for the placeholder until that module exists.</summary>
        public static string WhatIs(CompareContext context, WhatIsSubject subject)
        {
            ArgumentNullException.ThrowIfNull(context);
            return subject switch
            {
                WhatIsSubject.Source => $"What is the file {context.Path}, and what uses it?",
                WhatIsSubject.Application => $"Which application does {context.Path} belong to{(context.File.HasAppName ? $" ({context.File.AppName})" : string.Empty)}?",
                WhatIsSubject.Setting => $"What does the setting {context.Key} in {context.File.FileName} control?",
                _ => $"What does the value {context.Cell?.Value ?? "(masked)"} of {context.Key} mean?",
            };
        }

        public static BugReportDraft BugReport(CompareContext context) => new(context);
    }
}
