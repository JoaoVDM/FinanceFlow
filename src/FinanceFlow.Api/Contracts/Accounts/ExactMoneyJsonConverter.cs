using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FinanceFlow.Api.Contracts.Accounts;

// Validate the original JSON number before any decimal parser can round away a fraction.
public sealed class ExactMoneyJsonConverter : JsonConverter<decimal?>
{
    public override decimal? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;
        if (reader.TokenType != JsonTokenType.Number) throw new JsonException("A numeric amount with exact cents is required.");
        var raw = Encoding.UTF8.GetString(reader.HasValueSequence ? reader.ValueSequence.ToArray() : reader.ValueSpan);
        var negative = raw[0] == '-';
        var unsigned = negative ? raw[1..] : raw;
        var exponentIndex = unsigned.IndexOfAny(['e', 'E']);
        var mantissa = exponentIndex < 0 ? unsigned : unsigned[..exponentIndex];
        var dot = mantissa.IndexOf('.');
        var fractionLength = dot < 0 ? 0 : mantissa.Length - dot - 1;
        var digits = mantissa.Replace(".", string.Empty, StringComparison.Ordinal).TrimStart('0');
        if (digits.Length == 0) return 0m;
        var exponent = 0;
        if (exponentIndex >= 0 && !int.TryParse(unsigned.AsSpan(exponentIndex + 1), NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture, out exponent)) throw InvalidAmount();
        long shift = (long)exponent - fractionLength + 2;
        var trimmed = digits.TrimEnd('0');
        shift += digits.Length - trimmed.Length;
        digits = trimmed;
        if (shift < 0 || shift > 14 || digits.Length + shift > 14) throw InvalidAmount();
        var cents = long.Parse(digits + new string('0', (int)shift), CultureInfo.InvariantCulture);
        return (negative ? -cents : cents) / 100m;
    }

    private static JsonException InvalidAmount() => new("The amount must respect the monetary limits and exact cents.");
    public override void Write(Utf8JsonWriter writer, decimal? value, JsonSerializerOptions options)
    {
        if (value is { } amount) writer.WriteNumberValue(amount); else writer.WriteNullValue();
    }
}
