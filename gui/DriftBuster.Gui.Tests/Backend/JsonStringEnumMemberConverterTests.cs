using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using DriftBuster.Backend;
using DriftBuster.Backend.Models;

namespace DriftBuster.Gui.Tests.Backend;

public sealed class JsonStringEnumMemberConverterTests
{
    [Fact]
    public void CanConvert_handles_null_enum_and_nullable_enum_types()
    {
        var converter = CreateConverterFactory();

        converter.CanConvert(null!).Should().BeFalse();
        converter.CanConvert(typeof(ServerScanStatus)).Should().BeTrue();
        converter.CanConvert(typeof(ServerScanStatus?)).Should().BeTrue();
        converter.CanConvert(typeof(string)).Should().BeFalse();
    }

    [Fact]
    public void Deserialize_uses_enum_name_fallback_and_rejects_unknown_tokens()
    {
        var options = CreateSerializerOptions();

        var parsedByName = JsonSerializer.Deserialize<ServerScanStatus>("\"Succeeded\"", options);
        parsedByName.Should().Be(ServerScanStatus.Succeeded);

        var unknown = () => JsonSerializer.Deserialize<ServerScanStatus>("\"does-not-exist\"", options);
        unknown.Should().Throw<JsonException>();

        var nonStringToken = () => JsonSerializer.Deserialize<ServerScanStatus>("1", options);
        nonStringToken.Should().Throw<JsonException>();
    }

    [Fact]
    public void Serialize_uses_enum_member_text_and_falls_back_for_unknown_numeric_values()
    {
        var options = CreateSerializerOptions();

        var known = JsonSerializer.Serialize(ServerScanStatus.Cached, options);
        known.Should().Be("\"cached\"");

        var unknownNumeric = JsonSerializer.Serialize((ServerScanStatus)999, options);
        unknownNumeric.Should().Be("\"999\"");
    }

    private static JsonConverterFactory CreateConverterFactory()
    {
        var type = typeof(DriftbusterBackend).Assembly.GetType(
            "DriftBuster.Backend.JsonStringEnumMemberConverter",
            throwOnError: true)!;
        return (JsonConverterFactory)Activator.CreateInstance(type)!;
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(CreateConverterFactory());
        return options;
    }
}
