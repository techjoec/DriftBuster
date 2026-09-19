using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;

using Avalonia.Controls;

using DriftBuster.Gui.ViewModels;

namespace DriftBuster.Gui.Views
{
    /// <summary>
    /// The Compare view's right-click menu for a setting, one server's value of it, or a whole file. Every item calls the view
    /// model; dialogs and the clipboard go through the view.
    /// </summary>
    [ExcludeFromCodeCoverage]
    internal static class CompareContextMenu
    {
        public static ContextMenu Build(CompareView view, CompareViewModel viewModel, CompareContext context)
        {
            var items = new List<Control>
            {
                GroupMenu(view, viewModel, context),
                RuleMenu(view, viewModel, context),
                Item(context.Row is null ? "Mark every setting" : context.Row.IsMarked ? "Unmark" : "Mark", () => viewModel.ToggleMark(context)),
                CopyMenu(view, context),
                ViewMenu(view, viewModel, context),
                Item(context.Row?.InReview == true ? "Remove from report" : "Add to report", () => viewModel.ToggleReview(context)),
                new Separator(),
                IgnoreMenu(viewModel, context),
            };

            if (context.Row is not null)
            {
                var masked = context.Row.Cells.Any(cell => cell.IsMasked);
                items.Add(Sub(masked ? "Unmask values" : "Mask values", Lasting(persistence => viewModel.SetMasked(context, !masked, persistence))));
                items.Add(HistoryMenu(view, viewModel, context, "History"));
            }

            items.Add(new Separator());
            items.Add(WhatIsMenu(view, context));
            items.Add(Sub("Report bug",
                Item("GitHub issue…", () => _ = view.ShowAsync(new BugReportWindow(CompareViewModel.BugReport(context), preferApi: false))),
                Item("Send to receiver…", () => _ = view.ShowAsync(new BugReportWindow(CompareViewModel.BugReport(context), preferApi: true)))));
            return new ContextMenu { ItemsSource = items };
        }

        private static MenuItem GroupMenu(CompareView view, CompareViewModel viewModel, CompareContext context)
        {
            var items = viewModel.GroupsOf(context).Select(group => (Control)Sub(group,
                Item("View this group", () => viewModel.ViewGroup(group)),
                Item("Remove from this group", () => viewModel.RemoveFromGroup(context, group)))).ToList();
            if (items.Count > 0)
            {
                items.Add(new Separator());
            }

            items.Add(Item("Add to group…", () => _ = AddToGroupAsync(view, viewModel, context)));
            items.Add(Item("Manage groups…", () => _ = view.ShowManagerAsync(new CurationManagerViewModel(viewModel.Curation, viewModel.HostSetId, CurationManagerViewModel.GroupsTab))));
            return Sub("Group", items.ToArray());
        }

        private static async System.Threading.Tasks.Task AddToGroupAsync(CompareView view, CompareViewModel viewModel, CompareContext context)
        {
            var name = await view.ShowAsync<string>(new GroupPickerWindow($"Add {context.Key ?? context.Path} to a group", viewModel.GroupNames)).ConfigureAwait(true);
            if (!string.IsNullOrWhiteSpace(name))
            {
                viewModel.AddToGroup(context, name);
            }
        }

        private static MenuItem RuleMenu(CompareView view, CompareViewModel viewModel, CompareContext context)
        {
            var existing = viewModel.RuleNames.Select(name => (Control)Item(name, () => viewModel.AddToRule(context, name))).ToArray();
            return Sub("Rule",
                existing.Length == 0 ? Disabled("Add to existing rule (none yet)") : Sub("Add to existing rule", existing),
                Item("Create new rule…", () => _ = view.ShowManagerAsync(new CurationManagerViewModel(viewModel.Curation, viewModel.HostSetId, CurationManagerViewModel.RulesTab, viewModel.RuleDraftFor(context)))),
                Item("Manage rules…", () => _ = view.ShowManagerAsync(new CurationManagerViewModel(viewModel.Curation, viewModel.HostSetId, CurationManagerViewModel.RulesTab))));
        }

        private static MenuItem CopyMenu(CompareView view, CompareContext context)
        {
            if (context.Row is null)
            {
                return Item("Copy file path", () => _ = view.CopyAsync(context.Path));
            }

            var scopes = new List<(string Label, CompareCopyScope Scope)> { ("All", CompareCopyScope.All), ("Setting", CompareCopyScope.Setting) };
            if (context.Cell is not null)
            {
                scopes.Add(("This value", CompareCopyScope.ThisValue));
                scopes.Add(("All values", CompareCopyScope.Values));
            }
            else
            {
                scopes.Add(("Values", CompareCopyScope.Values));
            }

            Control Format(string label, CompareCopyFormat format) => Sub(label, scopes
                .Select(scope => (Control)Item(scope.Label, () => _ = view.CopyAsync(CompareViewModel.Copy(context, format, scope.Scope))))
                .ToArray());
            return Sub("Copy as",
                Format("JSON", CompareCopyFormat.Json),
                Format("TSV", CompareCopyFormat.Tsv),
                Format("Text", CompareCopyFormat.Text),
                Format("Hex", CompareCopyFormat.Hex));
        }

        private static MenuItem ViewMenu(CompareView view, CompareViewModel viewModel, CompareContext context)
        {
            var raw = viewModel.Servers.Select(server => (Control)Item(server.Label, () =>
            {
                var text = viewModel.RawText(context, server.HostId);
                _ = view.ShowAsync(new TextViewerWindow(
                    $"{context.File.FileName} on {server.Label}",
                    $"{context.Path} on {server.Label}",
                    text ?? string.Empty,
                    text is null ? "This copy's text is not at hand: the server has no copy, or the file was not part of a scan's details." : "The file as the scan read it, in canonical form."));
            })).ToArray();
            var items = new List<Control>
            {
                Item("As tree", () => _ = view.ShowAsync(new SettingsTreeWindow($"{context.Path} as a tree", viewModel.TreeOf(context.File)))),
                raw.Length == 0 ? Disabled("Raw data") : Sub("Raw data", raw),
            };
            if (context.Row is not null)
            {
                items.Add(HistoryMenu(view, viewModel, context, "History"));
            }

            return Sub("View", items.ToArray());
        }

        private static MenuItem HistoryMenu(CompareView view, CompareViewModel viewModel, CompareContext context, string header)
        {
            void Show(CompareHistoryKind kind)
            {
                try
                {
                    var history = viewModel.History(context, kind);
                    _ = view.ShowAsync(new HistoryWindow($"History of {context.Key} in {context.Path}", history, kind));
                }
                catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or System.IO.IOException or UnauthorizedAccessException)
                {
                    _ = view.ShowAsync(new TextViewerWindow("History", "History could not be read", ex.Message));
                }
            }

            return Sub(header,
                Item("Setting", () => Show(CompareHistoryKind.Setting)),
                Item("Value", () => Show(CompareHistoryKind.Value)),
                Item("Both", () => Show(CompareHistoryKind.Both)));
        }

        private static MenuItem IgnoreMenu(CompareViewModel viewModel, CompareContext context)
        {
            var items = new List<Control>();
            if (context.Row is not null)
            {
                items.Add(Sub("Setting", Lasting(persistence => viewModel.Ignore(context, CompareTargetLevel.Setting, persistence))));
                if (context.Cell?.ValueHash is not null)
                {
                    items.Add(Sub("This value", Lasting(persistence => viewModel.Ignore(context, CompareTargetLevel.Value, persistence))));
                }
            }

            items.Add(Sub("Source (the whole file)", Lasting(persistence => viewModel.Ignore(context, CompareTargetLevel.Source, persistence))));
            items.Add(new Separator());
            items.Add(Item("Forget this run's choices", viewModel.ClearRunChoices));
            return Sub("Ignore", items.ToArray());
        }

        private static MenuItem WhatIsMenu(CompareView view, CompareContext context)
        {
            void Show(WhatIsSubject subject) => _ = view.ShowAsync(new TextViewerWindow(
                "What is…",
                CompareViewModel.WhatIs(context, subject),
                string.Empty,
                "An assistant that explains files, applications, settings and values is planned. This is the question it will be asked."));
            var items = new List<Control> { Item("Source", () => Show(WhatIsSubject.Source)), Item("Application", () => Show(WhatIsSubject.Application)) };
            if (context.Row is not null)
            {
                items.Add(Item("Setting", () => Show(WhatIsSubject.Setting)));
                items.Add(Item("Value", () => Show(WhatIsSubject.Value)));
            }

            return Sub("What is…", items.ToArray());
        }

        private static Control[] Lasting(Action<CurationPersistence> choose) =>
        [
            Item("This run only", () => choose(CurationPersistence.ThisRun)),
            Item("Always", () => choose(CurationPersistence.AllRuns)),
            Item("Always, for these servers", () => choose(CurationPersistence.TheseServers)),
        ];

        private static MenuItem Sub(string header, params Control[] items) => new() { Header = header, ItemsSource = items };

        private static MenuItem Disabled(string header) => new() { Header = header, IsEnabled = false };

        private static MenuItem Item(string header, Action action)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => action();
            return item;
        }
    }
}
