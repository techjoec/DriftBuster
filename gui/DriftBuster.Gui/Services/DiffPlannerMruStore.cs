using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

using DriftBuster.Backend;

namespace DriftBuster.Gui.Services
{
    public sealed class DiffPlannerMruStore
    {
        public const int DefaultEntryLimit = 10;
        internal const int CurrentSchemaVersion = 2;

        private const string StoreFileName = "mru.json";
        private const string LegacyFileName = "diff-planner.json";

        private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        private static readonly JsonSerializerOptions LegacySerializerOptions = new(JsonSerializerDefaults.Web);

        private static readonly ConcurrentDictionary<string, SemaphoreSlim> PathLocks = new(StringComparer.OrdinalIgnoreCase);

        private readonly string _storePath;
        private readonly SemaphoreSlim _storeLock;
        private readonly Task _migrationTask;

        public DiffPlannerMruStore(string? rootDirectory = null)
            : this(
                rootDirectory,
                rootDirectory is null ? Path.Combine(DriftbusterPaths.GetCacheDirectory(), LegacyFileName) : null,
                null)
        {
        }

        internal DiffPlannerMruStore(
            string? rootDirectory,
            string? legacySettingsPath,
            Func<string, string, CancellationToken, Task>? migrationHandler)
        {
            var baseDirectory = ResolveBaseDirectory(rootDirectory);
            Directory.CreateDirectory(baseDirectory);

            _storePath = Path.GetFullPath(Path.Combine(baseDirectory, StoreFileName));
            _storeLock = GetLockForPath(_storePath);

            if (legacySettingsPath is null)
            {
                _migrationTask = Task.CompletedTask;
                return;
            }

            var migrate = migrationHandler ?? MigrateLegacySettingsAsync;
            var legacy = Path.GetFullPath(legacySettingsPath);
            _migrationTask = migrate(legacy, _storePath, CancellationToken.None);
        }

        public async Task<DiffPlannerMruSnapshot> LoadAsync(CancellationToken cancellationToken = default)
        {
            await _migrationTask.ConfigureAwait(false);

            await _storeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await LoadSnapshotUnsafeAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _storeLock.Release();
            }
        }

        public async Task SaveAsync(DiffPlannerMruSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            if (snapshot is null)
            {
                throw new ArgumentNullException(nameof(snapshot));
            }

            await _migrationTask.ConfigureAwait(false);

            var normalised = NormaliseSnapshot(snapshot, overrideLimit: null);

            await _storeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await WriteSnapshotUnsafeAsync(normalised, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _storeLock.Release();
            }
        }

        public async Task RecordAsync(DiffPlannerMruEntry entry, CancellationToken cancellationToken = default)
        {
            if (entry is null)
            {
                throw new ArgumentNullException(nameof(entry));
            }

            await _migrationTask.ConfigureAwait(false);

            var normalisedEntry = NormaliseEntry(entry);
            if (normalisedEntry is null)
            {
                return;
            }

            await _storeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var snapshot = await LoadSnapshotUnsafeAsync(cancellationToken).ConfigureAwait(false);
                var limit = snapshot.MaxEntries > 0
                    ? Math.Min(snapshot.MaxEntries, DefaultEntryLimit)
                    : DefaultEntryLimit;

                snapshot.Entries.RemoveAll(existing => AreEquivalent(existing, normalisedEntry));
                snapshot.Entries.Insert(0, normalisedEntry);

                if (snapshot.Entries.Count > limit)
                {
                    snapshot.Entries.RemoveRange(limit, snapshot.Entries.Count - limit);
                }

                snapshot.SchemaVersion = CurrentSchemaVersion;
                snapshot.MaxEntries = limit;

                var trimmed = NormaliseSnapshot(snapshot, overrideLimit: limit);
                await WriteSnapshotUnsafeAsync(trimmed, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _storeLock.Release();
            }
        }

        public async Task ClearAsync(CancellationToken cancellationToken = default)
        {
            await _migrationTask.ConfigureAwait(false);

            await _storeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (File.Exists(_storePath))
                {
                    File.Delete(_storePath);
                }
            }
            finally
            {
                _storeLock.Release();
            }
        }

        private async Task<DiffPlannerMruSnapshot> LoadSnapshotUnsafeAsync(CancellationToken cancellationToken)
        {
            if (!File.Exists(_storePath))
            {
                return CreateEmptySnapshot(DefaultEntryLimit);
            }

            await using var stream = new FileStream(
                _storePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                useAsync: true);

            try
            {
                var snapshot = await JsonSerializer
                    .DeserializeAsync<DiffPlannerMruSnapshot>(stream, SerializerOptions, cancellationToken)
                    .ConfigureAwait(false);

                return NormaliseSnapshot(snapshot, overrideLimit: null);
            }
            catch (JsonException)
            {
                return CreateEmptySnapshot(DefaultEntryLimit);
            }
        }

        private async Task WriteSnapshotUnsafeAsync(DiffPlannerMruSnapshot snapshot, CancellationToken cancellationToken)
        {
            var directory = Path.GetDirectoryName(_storePath)!;
            Directory.CreateDirectory(directory);

            await using var stream = new FileStream(
                _storePath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                useAsync: true);

            await JsonSerializer.SerializeAsync(stream, snapshot, SerializerOptions, cancellationToken).ConfigureAwait(false);
        }

        private static DiffPlannerMruSnapshot NormaliseSnapshot(
            DiffPlannerMruSnapshot? snapshot,
            int? overrideLimit)
        {
            var limit = overrideLimit ?? (snapshot?.MaxEntries > 0 ? snapshot.MaxEntries : DefaultEntryLimit);
            limit = Math.Clamp(limit, 1, DefaultEntryLimit);

            var normalised = CreateEmptySnapshot(limit);
            var entries = snapshot?.Entries;
            if (entries is { Count: > 0 })
            {
                foreach (var entry in entries)
                {
                    var normalisedEntry = NormaliseEntry(entry);
                    if (normalisedEntry is not null)
                    {
                        normalised.Entries.Add(normalisedEntry);
                    }
                }
            }

            normalised.Entries.Sort((left, right) => right.LastUsedUtc.CompareTo(left.LastUsedUtc));
            if (normalised.Entries.Count > limit)
            {
                normalised.Entries.RemoveRange(limit, normalised.Entries.Count - limit);
            }

            return normalised;
        }

        private static DiffPlannerMruEntry? NormaliseEntry(DiffPlannerMruEntry? entry)
        {
            if (entry is null)
            {
                return null;
            }

            var baseline = entry.BaselinePath?.Trim();
            if (string.IsNullOrWhiteSpace(baseline))
            {
                return null;
            }

            var comparisons = entry.ComparisonPaths?
                .Select(path => path?.Trim())
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => path!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList() ?? new List<string>();

            if (comparisons.Count == 0)
            {
                return null;
            }

            var displayName = string.IsNullOrWhiteSpace(entry.DisplayName)
                ? null
                : entry.DisplayName.Trim();

            var digest = string.IsNullOrWhiteSpace(entry.SanitizedDigest)
                ? null
                : entry.SanitizedDigest.Trim();

            var payloadKind = Enum.IsDefined(typeof(DiffPlannerPayloadKind), entry.PayloadKind)
                ? entry.PayloadKind
                : DiffPlannerPayloadKind.Unknown;

            var lastUsed = entry.LastUsedUtc == default
                ? DateTimeOffset.UtcNow
                : entry.LastUsedUtc.ToUniversalTime();

            return new DiffPlannerMruEntry
            {
                BaselinePath = baseline!,
                ComparisonPaths = comparisons,
                DisplayName = displayName,
                LastUsedUtc = lastUsed,
                PayloadKind = payloadKind,
                SanitizedDigest = digest,
            };
        }

        private static DiffPlannerMruSnapshot CreateEmptySnapshot(int limit)
        {
            var boundedLimit = Math.Clamp(limit, 1, DefaultEntryLimit);
            return new DiffPlannerMruSnapshot
            {
                SchemaVersion = CurrentSchemaVersion,
                MaxEntries = boundedLimit,
                Entries = new List<DiffPlannerMruEntry>(boundedLimit),
            };
        }

        private static bool AreEquivalent(DiffPlannerMruEntry left, DiffPlannerMruEntry right)
        {
            if (!string.Equals(left.BaselinePath, right.BaselinePath, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (left.ComparisonPaths.Count != right.ComparisonPaths.Count)
            {
                return false;
            }

            for (var index = 0; index < left.ComparisonPaths.Count; index++)
            {
                if (!string.Equals(
                        left.ComparisonPaths[index],
                        right.ComparisonPaths[index],
                        StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            return true;
        }

        private static string ResolveBaseDirectory(string? rootDirectory)
        {
            if (string.IsNullOrWhiteSpace(rootDirectory))
            {
                return DriftbusterPaths.GetCacheDirectory("diff-planner");
            }

            var resolved = Path.GetFullPath(rootDirectory);
            return Path.Combine(resolved, "diff-planner");
        }

        private static SemaphoreSlim GetLockForPath(string path)
        {
            return PathLocks.GetOrAdd(path, static _ => new SemaphoreSlim(1, 1));
        }

        private static async Task MigrateLegacySettingsAsync(
            string legacyPath,
            string destinationPath,
            CancellationToken cancellationToken)
        {
            try
            {
                if (!File.Exists(legacyPath))
                {
                    return;
                }

                if (File.Exists(destinationPath))
                {
                    return;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);

                await using var source = new FileStream(
                    legacyPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 4096,
                    useAsync: true);

                var legacy = await JsonSerializer
                    .DeserializeAsync<LegacyDiffPlannerSettings>(source, LegacySerializerOptions, cancellationToken)
                    .ConfigureAwait(false);

                if (legacy is null)
                {
                    return;
                }

                var entries = legacy.ResolveEntries()
                    .Select(entry => entry.ToEntry())
                    .Where(entry => entry is not null)
                    .Select(NormaliseEntry)
                    .Where(entry => entry is not null)
                    .Cast<DiffPlannerMruEntry>()
                    .ToList();

                if (entries.Count == 0)
                {
                    return;
                }

                var limit = legacy.ResolveEntryLimit();
                var snapshot = CreateEmptySnapshot(limit);
                snapshot.Entries.AddRange(entries.Take(snapshot.MaxEntries));

                await using var target = new FileStream(
                    destinationPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 4096,
                    useAsync: true);

                await JsonSerializer.SerializeAsync(target, snapshot, SerializerOptions, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                // Best-effort migration. Failures should not block the new storage path.
            }
        }

        private sealed class LegacyDiffPlannerSettings
        {
            [JsonPropertyName("schema_version")]
            public int? SchemaVersion { get; set; }

            [JsonPropertyName("max_entries")]
            public int? MaxEntries { get; set; }

            [JsonPropertyName("entries")]
            public List<LegacyDiffPlannerEntry>? Entries { get; set; }

            [JsonPropertyName("baseline")]
            public string? Baseline { get; set; }

            [JsonPropertyName("baseline_path")]
            public string? BaselinePath { get; set; }

            [JsonPropertyName("comparisons")]
            public List<string>? Comparisons { get; set; }

            [JsonPropertyName("comparison_paths")]
            public List<string>? ComparisonPaths { get; set; }

            [JsonPropertyName("label")]
            public string? Label { get; set; }

            [JsonPropertyName("display_name")]
            public string? DisplayName { get; set; }

            [JsonPropertyName("last_used")]
            public DateTimeOffset? LastUsedUtc { get; set; }

            [JsonPropertyName("last_used_utc")]
            public DateTimeOffset? LastUsedUtcAlternative { get; set; }

            [JsonPropertyName("payload_kind")]
            public string? PayloadKind { get; set; }

            [JsonPropertyName("sanitized_digest")]
            public string? SanitizedDigest { get; set; }

            public IEnumerable<LegacyDiffPlannerEntry> ResolveEntries()
            {
                if (Entries is { Count: > 0 })
                {
                    return Entries;
                }

                var baseline = BaselinePath ?? Baseline;
                var comparisons = ComparisonPaths ?? Comparisons ?? new List<string>();
                if (string.IsNullOrWhiteSpace(baseline) || comparisons.Count == 0)
                {
                    return Array.Empty<LegacyDiffPlannerEntry>();
                }

                return new[]
                {
                    new LegacyDiffPlannerEntry
                    {
                        BaselinePath = baseline!,
                        ComparisonPaths = comparisons,
                        DisplayName = DisplayName ?? Label,
                        LastUsedUtc = LastUsedUtcAlternative ?? LastUsedUtc,
                        PayloadKind = PayloadKind,
                        SanitizedDigest = SanitizedDigest,
                    },
                };
            }

            public int ResolveEntryLimit()
            {
                if (MaxEntries is { } limit && limit > 0)
                {
                    return Math.Min(limit, DefaultEntryLimit);
                }

                return DefaultEntryLimit;
            }
        }

        private sealed class LegacyDiffPlannerEntry
        {
            [JsonPropertyName("baseline")]
            public string? Baseline { get; set; }

            [JsonPropertyName("baseline_path")]
            public string? BaselinePath { get; set; }

            [JsonPropertyName("comparison")]
            public string? Comparison { get; set; }

            [JsonPropertyName("comparison_paths")]
            public List<string>? ComparisonPaths { get; set; }

            [JsonPropertyName("comparisons")]
            public List<string>? Comparisons { get; set; }

            [JsonPropertyName("display_name")]
            public string? DisplayName { get; set; }

            [JsonPropertyName("label")]
            public string? Label { get; set; }

            [JsonPropertyName("last_used")]
            public DateTimeOffset? LastUsedUtc { get; set; }

            [JsonPropertyName("payload_kind")]
            public string? PayloadKind { get; set; }

            [JsonPropertyName("sanitized_digest")]
            public string? SanitizedDigest { get; set; }

            public DiffPlannerMruEntry? ToEntry()
            {
                var baseline = BaselinePath ?? Baseline;
                if (string.IsNullOrWhiteSpace(baseline))
                {
                    return null;
                }

                var comparisons = ComparisonPaths ?? Comparisons ??
                    (Comparison is null ? new List<string>() : new List<string> { Comparison });
                if (comparisons.Count == 0)
                {
                    return null;
                }

                var kind = ParsePayloadKind(PayloadKind);

                return new DiffPlannerMruEntry
                {
                    BaselinePath = baseline!,
                    ComparisonPaths = comparisons,
                    DisplayName = DisplayName ?? Label,
                    LastUsedUtc = LastUsedUtc ?? DateTimeOffset.UtcNow,
                    PayloadKind = kind,
                    SanitizedDigest = SanitizedDigest,
                };
            }

            private static DiffPlannerPayloadKind ParsePayloadKind(string? value)
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    return DiffPlannerPayloadKind.Unknown;
                }

                return value.Trim().ToLowerInvariant() switch
                {
                    "sanitized" => DiffPlannerPayloadKind.Sanitized,
                    "raw" => DiffPlannerPayloadKind.Raw,
                    _ => DiffPlannerPayloadKind.Unknown,
                };
            }
        }
    }

    public sealed class DiffPlannerMruSnapshot
    {
        [JsonPropertyName("schema_version")]
        public int SchemaVersion { get; set; } = DiffPlannerMruStore.CurrentSchemaVersion;

        [JsonPropertyName("max_entries")]
        public int MaxEntries { get; set; } = DiffPlannerMruStore.DefaultEntryLimit;

        [JsonPropertyName("entries")]
        public List<DiffPlannerMruEntry> Entries { get; set; } = new();
    }

    public sealed class DiffPlannerMruEntry
    {
        [JsonPropertyName("baseline_path")]
        public string BaselinePath { get; set; } = string.Empty;

        [JsonPropertyName("comparison_paths")]
        public List<string> ComparisonPaths { get; set; } = new();

        [JsonPropertyName("display_name")]
        public string? DisplayName { get; set; }

        [JsonPropertyName("last_used_utc")]
        public DateTimeOffset LastUsedUtc { get; set; } = DateTimeOffset.UtcNow;

        [JsonPropertyName("payload_kind")]
        public DiffPlannerPayloadKind PayloadKind { get; set; } = DiffPlannerPayloadKind.Unknown;

        [JsonPropertyName("sanitized_digest")]
        public string? SanitizedDigest { get; set; }
    }

    public enum DiffPlannerPayloadKind
    {
        Unknown = 0,
        Sanitized = 1,
        Raw = 2,
    }
}
