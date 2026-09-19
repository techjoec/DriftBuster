using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;

using Avalonia.Controls;
using Avalonia.Input.Platform;

using DriftBuster.Gui.ViewModels;

namespace DriftBuster.Gui.Views
{
    /// <summary>Dialogs and the clipboard for any view: shown over the window the anchor control is in.</summary>
    [ExcludeFromCodeCoverage]
    internal static class DialogHost
    {
        /// <summary>Shows a dialog over the anchor's window, or on its own when there is none.</summary>
        public static async Task<T?> ShowAsync<T>(Control anchor, Window dialog)
        {
            if (TopLevel.GetTopLevel(anchor) is Window owner)
            {
                return await dialog.ShowDialog<T?>(owner).ConfigureAwait(true);
            }

            dialog.Show();
            return default;
        }

        public static Task ShowAsync(Control anchor, Window dialog) => ShowAsync<object>(anchor, dialog);

        /// <summary>Shows Manage choices, then clears the comparison's status line, which no longer describes what is on screen.</summary>
        public static async Task ShowManagerAsync(Control anchor, CompareViewModel viewModel, CurationManagerViewModel manager)
        {
            await ShowAsync(anchor, new CurationManagerWindow(manager)).ConfigureAwait(true);
            viewModel.StatusMessage = string.Empty;
        }

        public static async Task CopyAsync(Control anchor, string text)
        {
            if (TopLevel.GetTopLevel(anchor)?.Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync(text).ConfigureAwait(true);
            }
        }
    }
}
