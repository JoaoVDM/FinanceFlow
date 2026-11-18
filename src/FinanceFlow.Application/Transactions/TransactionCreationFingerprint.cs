using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using FinanceFlow.Domain.Transactions;

namespace FinanceFlow.Application.Transactions;

public static class TransactionCreationFingerprint
{
    public const string Operation = "transactions.create.v1";

    // Frozen field order and representation. Description is preserved exactly, including null versus empty.
    public static byte[] Compute(FinancialTransaction transaction)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("format", Operation);
            writer.WriteString("accountId", transaction.AccountId.ToString("D"));
            writer.WriteString("categoryId", transaction.CategoryId.ToString("D"));
            writer.WriteString("kind", transaction.Kind);
            writer.WriteString("amount", transaction.Amount.ToString("F2", CultureInfo.InvariantCulture));
            writer.WriteString("occurredOn", transaction.OccurredOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            writer.WriteString("description", transaction.Description);
            writer.WriteEndObject();
        }

        return SHA256.HashData(buffer.ToArray());
    }
}
