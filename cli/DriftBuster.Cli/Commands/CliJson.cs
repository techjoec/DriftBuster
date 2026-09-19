using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

using DriftBuster.Backend.Json;

namespace DriftBuster.Cli.Commands;

/// <summary>The console tool's JSON output from <see cref="CliJsonContext"/>: one line, non-ASCII kept.</summary>
internal static class CliJson
{
    private static readonly JsonSerializerOptions Options = ModelJson.CreateOptions(CliJsonContext.Default, indented: false);

    public static string Line<T>(T value) => JsonSerializer.Serialize(value, (JsonTypeInfo<T>)Options.GetTypeInfo(typeof(T)));
}
