using System.Text.Json.Serialization;

using DriftBuster.Backend.MultiServer;

namespace DriftBuster.Cli.Commands;

/// <summary>Source-generated, strict contracts for what the console tool reads (versions.json, the multi-server request) and its multi-server lines.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    AllowDuplicateProperties = false,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(ComponentVersions))]
[JsonSerializable(typeof(MultiServerRequest))]
[JsonSerializable(typeof(MultiServerLine))]
[JsonSerializable(typeof(SelfcheckReport))]
internal sealed partial class CliJsonContext : JsonSerializerContext;
