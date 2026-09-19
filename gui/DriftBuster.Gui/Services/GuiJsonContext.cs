using System.Text.Json.Serialization;

using DriftBuster.Backend.Json;

namespace DriftBuster.Gui.Services
{
    /// <summary>The GUI's own files, with the same strict settings as the backend's <see cref="ModelJson"/>.</summary>
    [JsonSourceGenerationOptions(
        PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
        UseStringEnumConverter = true,
        WriteIndented = true,
        NewLine = "\n",
        AllowDuplicateProperties = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true)]
    [JsonSerializable(typeof(ServerSelectionCache))]
    [JsonSerializable(typeof(DiffPlannerMruSnapshot))]
    internal sealed partial class GuiJsonContext : JsonSerializerContext;

}
