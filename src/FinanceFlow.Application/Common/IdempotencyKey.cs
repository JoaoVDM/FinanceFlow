namespace FinanceFlow.Application.Common;

public static class IdempotencyKey
{
    public static bool IsValid(string? key) => key is { Length: >= 1 and <= 128 }
        && key.All(character => character is >= '!' and <= '~');
}
