using System.ComponentModel.DataAnnotations;

namespace FinanceFlow.Application.Users;

public static class AccountInputRules
{
    public static bool IsPasswordValid(string? value) =>
        value is { Length: >= 12 and <= 128 } && !string.IsNullOrWhiteSpace(value);
    public static bool IsDisplayNameValid(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 100;
    public static bool IsEmailValid(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 256 && new EmailAddressAttribute().IsValid(value);
}
