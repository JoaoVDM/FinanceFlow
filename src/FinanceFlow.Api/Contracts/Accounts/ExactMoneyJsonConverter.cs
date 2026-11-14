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
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType != JsonTokenType.Number)
        {
            throw new JsonException("A numeric amount with exact cents is required.");
        }

        var numberText = Encoding.UTF8.GetString(reader.HasValueSequence ? reader.ValueSequence.ToArray() : reader.ValueSpan);
        var isNegative = numberText[0] == '-';
        var unsignedNumberText = isNegative ? numberText[1..] : numberText;
        var exponentIndex = unsignedNumberText.IndexOfAny(['e', 'E']);
        var mantissa = exponentIndex < 0 ? unsignedNumberText : unsignedNumberText[..exponentIndex];
        var decimalPointIndex = mantissa.IndexOf('.');
        var fractionalDigitCount = decimalPointIndex < 0 ? 0 : mantissa.Length - decimalPointIndex - 1;
        var coefficientDigits = mantissa.Replace(".", string.Empty, StringComparison.Ordinal).TrimStart('0');

        // Zero is exact regardless of the exponent, including exponents outside Int32.
        if (coefficientDigits.Length == 0)
        {
            return 0m;
        }

        var exponent = 0;
        if (exponentIndex >= 0 && !int.TryParse(unsignedNumberText.AsSpan(exponentIndex + 1), NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture, out exponent))
        {
            throw InvalidAmount();
        }

        // Integer cents = coefficient * 10^(exponent - fractional digits + 2).
        // Absorb trailing zeros before rejecting any remaining fraction of a cent.
        long centScaleShift = (long)exponent - fractionalDigitCount + 2;
        var digitsWithoutTrailingZeros = coefficientDigits.TrimEnd('0');
        centScaleShift += coefficientDigits.Length - digitsWithoutTrailingZeros.Length;
        coefficientDigits = digitsWithoutTrailingZeros;

        // The approved maximum is 14 digits in cents. Check before allocating zeros.
        if (centScaleShift < 0 || centScaleShift > 14 || coefficientDigits.Length + centScaleShift > 14)
        {
            throw InvalidAmount();
        }

        var cents = long.Parse(coefficientDigits + new string('0', (int)centScaleShift), CultureInfo.InvariantCulture);
        return (isNegative ? -cents : cents) / 100m;
    }

    private static JsonException InvalidAmount() => new("The amount must respect the monetary limits and exact cents.");

    public override void Write(Utf8JsonWriter writer, decimal? value, JsonSerializerOptions options)
    {
        if (value is { } amount)
        {
            writer.WriteNumberValue(amount);
        }
        else
        {
            writer.WriteNullValue();
        }
    }
}
