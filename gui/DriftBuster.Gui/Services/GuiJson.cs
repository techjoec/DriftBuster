using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

using DriftBuster.Backend.Json;

namespace DriftBuster.Gui.Services
{
    /// <summary>Contracts for the GUI's own files, from <see cref="GuiJsonContext"/>.</summary>
    internal static class GuiJson
    {
        private static readonly JsonSerializerOptions Options = ModelJson.CreateOptions(GuiJsonContext.Default);

        public static JsonTypeInfo<T> TypeInfo<T>() => (JsonTypeInfo<T>)Options.GetTypeInfo(typeof(T));
    }
}
