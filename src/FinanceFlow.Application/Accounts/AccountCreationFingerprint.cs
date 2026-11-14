using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using FinanceFlow.Domain.Accounts;

namespace FinanceFlow.Application.Accounts;

public static class AccountCreationFingerprint
{
    public const string Operation = "accounts.create.v1";

    public static bool IsKeyValid(string? key) => key is { Length: >= 1 and <= 128 }
        && key.All(character => character is >= '!' and <= '~');

    // Frozen v1 canonical representation, independent of HTTP JSON property order/culture.
    public static byte[] Compute(FinancialAccount account)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("format", Operation);
            writer.WriteString("name", account.Name);
            writer.WriteString("type", account.Type);
            writer.WriteString("currency", account.Currency);
            writer.WriteString("openingBalance", account.OpeningBalance.ToString("F2", CultureInfo.InvariantCulture));
            writer.WriteString("openingDate", account.OpeningDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            writer.WriteEndObject();
        }
        return SHA256.HashData(buffer.ToArray());
    }
}
