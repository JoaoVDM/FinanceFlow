namespace FinanceFlow.Domain.Common;

public static class MonetaryLimits
{
    public const decimal MaximumAmount = 999999999999.99m;

    public static bool IsPositiveAmount(decimal amount) =>
        amount > 0m && amount <= MaximumAmount && amount % 0.01m == 0m;
}
