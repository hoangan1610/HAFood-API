// =======================================
// File: HAShop.Api/Payments/JsonConverters.cs
// =======================================
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HAShop.Api.Payments;

public sealed class StringOrNumberConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number => reader.TryGetInt64(out var l)
                ? l.ToString(CultureInfo.InvariantCulture)
                : reader.GetDecimal().ToString(CultureInfo.InvariantCulture),
            JsonTokenType.Null => null,
            _ => throw new JsonException($"Expected string/number but got {reader.TokenType}")
        };

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
        => writer.WriteStringValue(value);
}

public sealed class StringOrNumberLongConverter : JsonConverter<long>
{
    public override long Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType switch
        {
            JsonTokenType.Number => reader.GetInt64(),
            JsonTokenType.String => long.TryParse(reader.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var v)
                ? v
                : throw new JsonException("Invalid long string"),
            _ => throw new JsonException($"Expected string/number but got {reader.TokenType}")
        };

    public override void Write(Utf8JsonWriter writer, long value, JsonSerializerOptions options)
        => writer.WriteNumberValue(value);
}
