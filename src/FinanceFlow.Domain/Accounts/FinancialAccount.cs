using System.Text;
using System.Globalization;

namespace FinanceFlow.Domain.Accounts;

public sealed class FinancialAccount
{
    public const decimal MaximumOpeningBalance = 999999999999.99m;
    private FinancialAccount() { }

    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string Type { get; private set; } = string.Empty;
    public string Currency { get; private set; } = string.Empty;
    public decimal OpeningBalance { get; private set; }
    public DateOnly OpeningDate { get; private set; }
    public bool IsActive { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset? UpdatedAtUtc { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public static bool IsTypeValid(string? type) => type is "checking" or "savings" or "cash" or "other";

    public static FinancialAccount Create(Guid id, Guid userId, string name, string type, string currency,
        decimal openingBalance, DateOnly openingDate, DateOnly today, DateTimeOffset createdAt)
    {
        if (id == Guid.Empty) throw new ArgumentException("An account identifier is required.", nameof(id));
        if (userId == Guid.Empty) throw new ArgumentException("An owner is required.", nameof(userId));
        if (string.IsNullOrWhiteSpace(name) || name.Length > 100)
            throw new ArgumentException("The name must contain 1 to 100 characters.", nameof(name));
        var normalizedName = name.Trim().Normalize(NormalizationForm.FormC);
        if (normalizedName.Length is 0 or > 100) throw new ArgumentException("Invalid name.", nameof(name));
        if (!IsTypeValid(type)) throw new ArgumentException("Invalid account type.", nameof(type));
        if (currency != "BRL") throw new ArgumentException("Only BRL is supported.", nameof(currency));
        if (openingBalance < -MaximumOpeningBalance || openingBalance > MaximumOpeningBalance || openingBalance % 0.01m != 0m)
            throw new ArgumentException("The opening balance must respect the monetary limits and exact cents.", nameof(openingBalance));
        if (openingDate > today) throw new ArgumentException("The opening date cannot be in the future.", nameof(openingDate));
        return new FinancialAccount
        {
            Id = id, UserId = userId, Name = normalizedName, Type = type, Currency = currency,
            // Exact cents have already been validated; this only fixes the stored decimal scale.
            OpeningBalance = decimal.Parse((openingBalance == 0m ? 0m : openingBalance).ToString("F2", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture),
            OpeningDate = openingDate, IsActive = true, CreatedAtUtc = createdAt.ToUniversalTime()
        };
    }
}
