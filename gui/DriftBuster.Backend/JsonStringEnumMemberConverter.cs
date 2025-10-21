using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DriftBuster.Backend
{
    internal sealed class JsonStringEnumMemberConverter : JsonConverterFactory
    {
        public override bool CanConvert(Type typeToConvert)
        {
            if (typeToConvert == null)
            {
                return false;
            }

            if (typeToConvert.IsEnum)
            {
                return true;
            }

            var underlying = Nullable.GetUnderlyingType(typeToConvert);
            return underlying?.IsEnum == true;
        }

        public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
        {
            var targetType = Nullable.GetUnderlyingType(typeToConvert) ?? typeToConvert;
            var converterType = typeof(EnumMemberConverter<>).MakeGenericType(targetType);
            return (JsonConverter)Activator.CreateInstance(converterType)!;
        }

        private sealed class EnumMemberConverter<TEnum> : JsonConverter<TEnum> where TEnum : struct, Enum
        {
            private readonly Dictionary<string, TEnum> _readLookup;
            private readonly Dictionary<TEnum, string> _writeLookup;

            public EnumMemberConverter()
            {
                _readLookup = new Dictionary<string, TEnum>(StringComparer.OrdinalIgnoreCase);
                _writeLookup = new Dictionary<TEnum, string>();

                foreach (var field in typeof(TEnum).GetFields(BindingFlags.Public | BindingFlags.Static))
                {
                    var enumValue = (TEnum)field.GetValue(null)!;
                    var attribute = field.GetCustomAttribute<EnumMemberAttribute>();
                    var text = attribute?.Value ?? field.Name;
                    _readLookup[text] = enumValue;
                    _writeLookup[enumValue] = text;
                }
            }

            public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            {
                if (reader.TokenType != JsonTokenType.String)
                {
                    throw new JsonException($"Expected string for enum {typeof(TEnum).Name}.");
                }

                var text = reader.GetString();
                if (text is not null && _readLookup.TryGetValue(text, out var value))
                {
                    return value;
                }

                if (text is not null && Enum.TryParse(text, ignoreCase: true, out value))
                {
                    return value;
                }

                throw new JsonException($"Unknown value '{text}' for enum {typeof(TEnum).Name}.");
            }

            public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options)
            {
                if (_writeLookup.TryGetValue(value, out var text))
                {
                    writer.WriteStringValue(text);
                }
                else
                {
                    writer.WriteStringValue(value.ToString());
                }
            }
        }
    }
}
