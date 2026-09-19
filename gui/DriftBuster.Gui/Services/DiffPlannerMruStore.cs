using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using DriftBuster.Backend;
using DriftBuster.Backend.Json;

namespace DriftBuster.Gui.Services
{
    /// <summary>
    /// The Diff planner's recent file sets in <c>diff-planner/mru.json</c> under the cache directory, newest first and at most
    /// <see cref="DefaultEntryLimit"/>. Read strictly: a file that cannot be read raises <see cref="InvalidDataException"/> naming the
    /// file and JSON path, and is never replaced.
    /// </summary>
    public sealed class DiffPlannerMruStore
    {
        public const int DefaultEntryLimit = 10;
        internal const int CurrentSchemaVersion = 3;
        internal const string FileName = "mru.json";

        private readonly SemaphoreSlim _lock = new(1, 1);

        public DiffPlannerMruStore(string? rootDirectory = null)
        {
            var directory = string.IsNullOrWhiteSpace(rootDirectory)
                ? DriftbusterPaths.GetCacheDirectory("diff-planner")
                : Path.Join(Path.GetFullPath(rootDirectory), "diff-planner");
            StorePath = Path.GetFullPath(Path.Join(directory, FileName));
        }

        public string StorePath { get; }

        /// <summary>The saved entries, or none when there is no file.</summary>
        public DiffPlannerMruSnapshot Read()
        {
            if (!File.Exists(StorePath))
            {
                return new DiffPlannerMruSnapshot();
            }

            var snapshot = ModelJson.ReadFile(StorePath, GuiJson.TypeInfo<DiffPlannerMruSnapshot>());
            return snapshot.SchemaVersion == CurrentSchemaVersion
                ? snapshot
                : throw new InvalidDataException($"{StorePath}: $.schema_version: {snapshot.SchemaVersion} is not the supported version {CurrentSchemaVersion}.");
        }

        public async Task<DiffPlannerMruSnapshot> LoadAsync(CancellationToken cancellationToken = default)
        {
            await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return Read();
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>Writes the entries, newest first, without blank paths or repeats, cut to <see cref="DefaultEntryLimit"/>.</summary>
        public async Task SaveAsync(DiffPlannerMruSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                Write(snapshot);
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>Puts the entry first, replacing an entry for the same files; an entry without a baseline or comparison is ignored.</summary>
        public async Task RecordAsync(DiffPlannerMruEntry entry, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(entry);
            if (Normalise(entry) is not { } normalised)
            {
                return;
            }

            await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var current = Read();
                Write(current with { Entries = [normalised, .. current.Entries.Where(existing => !SameFiles(existing, normalised))] });
            }
            finally
            {
                _lock.Release();
            }
        }

        public async Task ClearAsync(CancellationToken cancellationToken = default)
        {
            await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (File.Exists(StorePath))
                {
                    File.Delete(StorePath);
                }
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>True when both entries name the same baseline and comparisons in the same order, ignoring case.</summary>
        public static bool SameFiles(DiffPlannerMruEntry left, DiffPlannerMruEntry right)
        {
            ArgumentNullException.ThrowIfNull(left);
            ArgumentNullException.ThrowIfNull(right);
            return string.Equals(left.BaselinePath, right.BaselinePath, StringComparison.OrdinalIgnoreCase)
                && left.ComparisonPaths.SequenceEqual(right.ComparisonPaths, StringComparer.OrdinalIgnoreCase);
        }

        private void Write(DiffPlannerMruSnapshot snapshot)
        {
            var entries = snapshot.Entries
                .Select(Normalise)
                .OfType<DiffPlannerMruEntry>()
                .OrderByDescending(entry => entry.LastUsedUtc)
                .Take(DefaultEntryLimit)
                .ToArray();
            ModelJson.WriteFile(StorePath, new DiffPlannerMruSnapshot { Entries = entries }, GuiJson.TypeInfo<DiffPlannerMruSnapshot>());
        }

        private static DiffPlannerMruEntry? Normalise(DiffPlannerMruEntry entry)
        {
            var baseline = entry.BaselinePath.Trim();
            string[] comparisons = [.. entry.ComparisonPaths
                .Select(path => path.Trim())
                .Where(path => path.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)];
            return baseline.Length == 0 || comparisons.Length == 0
                ? null
                : entry with
                {
                    BaselinePath = baseline,
                    ComparisonPaths = comparisons,
                    DisplayName = string.IsNullOrWhiteSpace(entry.DisplayName) ? null : entry.DisplayName.Trim(),
                    SanitizedDigest = string.IsNullOrWhiteSpace(entry.SanitizedDigest) ? null : entry.SanitizedDigest.Trim(),
                    LastUsedUtc = entry.LastUsedUtc.ToUniversalTime(),
                };
        }
    }
}
