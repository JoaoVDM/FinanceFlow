using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using FinanceFlow.Domain.Transfers;

namespace FinanceFlow.Application.Transfers;

public static class TransferCreationFingerprint
{
    public const string Operation = "transfers.create.v1";

    public static byte[] Compute(Transfer transfer)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            // Frozen order and decimal representation; description is preserved, including null/empty.
            writer.WriteStartObject();
            writer.WriteString("format", Operation);
            writer.WriteString("sourceAccountId", transfer.SourceAccountId.ToString("D"));
            writer.WriteString("destinationAccountId", transfer.DestinationAccountId.ToString("D"));
            writer.WriteString("amount", transfer.Amount.ToString("F2", CultureInfo.InvariantCulture));
            writer.WriteString("occurredOn", transfer.OccurredOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            writer.WriteString("description", transfer.Description);
            writer.WriteEndObject();
        }
        return SHA256.HashData(buffer.ToArray());
    }
}
