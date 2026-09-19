using System.Text.Json.Serialization;

using DriftBuster.Backend.Curation;
using DriftBuster.Backend.Models;
using DriftBuster.Backend.MultiServer;
using DriftBuster.Backend.Profiles.Detection;
using DriftBuster.Backend.Profiles.Run;
using DriftBuster.Backend.Registry;
using DriftBuster.Backend.Remote;
using DriftBuster.Backend.Scheduling;
using DriftBuster.Backend.Secrets;
using DriftBuster.Backend.Sql;

namespace DriftBuster.Backend.Json;

/// <summary>The source-generated contracts behind <see cref="ModelJson"/>; every model read from or written to JSON is listed here.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    UseStringEnumConverter = true,
    WriteIndented = true,
    NewLine = "\n",
    AllowDuplicateProperties = false,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(CurationDocument))]
[JsonSerializable(typeof(RunProfileDefinition))]
[JsonSerializable(typeof(RunProfileListResult))]
[JsonSerializable(typeof(RunProfileRunResult))]
[JsonSerializable(typeof(OfflineRunnerConfig))]
[JsonSerializable(typeof(DetectionProfileStoreFile))]
[JsonSerializable(typeof(DetectionProfileSummary))]
[JsonSerializable(typeof(DetectionProfileSummaryDiff))]
[JsonSerializable(typeof(HuntBridgeResult))]
[JsonSerializable(typeof(CaptureSnapshot))]
[JsonSerializable(typeof(CaptureManifest))]
[JsonSerializable(typeof(CaptureComparison))]
[JsonSerializable(typeof(SqlSnapshot))]
[JsonSerializable(typeof(SqlExportManifest))]
[JsonSerializable(typeof(DiffCacheEntry))]
[JsonSerializable(typeof(RegistryScanConfig))]
[JsonSerializable(typeof(SecretRuleFile))]
[JsonSerializable(typeof(MultiServerRequest))]
[JsonSerializable(typeof(ScheduleManifest))]
[JsonSerializable(typeof(IReadOnlyDictionary<string, ScheduleStateEntry>))]
[JsonSerializable(typeof(ScheduleListResult))]
[JsonSerializable(typeof(ScheduleStatusListResult))]
[JsonSerializable(typeof(ScheduleDueResult))]
[JsonSerializable(typeof(ScheduleStateResult))]
[JsonSerializable(typeof(IReadOnlyList<ScheduleStatus>))]
[JsonSerializable(typeof(IReadOnlyList<ScheduleDueRun>))]
internal sealed partial class ModelJsonContext : JsonSerializerContext;
