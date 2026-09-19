using System;
using System.IO;

namespace DriftBuster.Gui.Services
{
    /// <summary>
    /// DriftBuster's own files the GUI reads (the Multi-server session, the Diff planner's recent files, the saved curation), checked
    /// before the main window opens: while one cannot be read the app does not start, so nothing replaces it.
    /// </summary>
    public static class OwnFileCheck
    {
        /// <summary>Why the files under the data root cannot be used, or null when every one of them reads.</summary>
        public static string? Run() => FirstFailure(
            () => new SessionCacheService().Read(),
            () => new DiffPlannerMruStore().Read(),
            () => CurationService.Shared.Document);

        /// <summary>The message of the first read that fails with a data or I/O error, or null.</summary>
        public static string? FirstFailure(params Func<object?>[] reads)
        {
            ArgumentNullException.ThrowIfNull(reads);
            foreach (var read in reads)
            {
                try
                {
                    _ = read();
                }
                catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
                {
                    return ex.Message;
                }
            }

            return null;
        }
    }
}
