using System.Text.Json.Serialization;

namespace DriftBuster.Cli.Commands;

/// <summary>Source-generated, strict contracts for the files the build commands read.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    AllowDuplicateProperties = false,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(ComponentVersions))]
internal sealed partial class CliJsonContext : JsonSerializerContext;
