using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace FinanceFlow.Api.Contracts.Accounts;

public sealed class CreateAccountRequest
{
    [Required, StringLength(100)]
    public string Name { get; init; } = string.Empty;

    [Required]
    public string Type { get; init; } = string.Empty;

    [Required]
    public string Currency { get; init; } = string.Empty;

    [Required, JsonConverter(typeof(ExactMoneyJsonConverter))]
    public decimal? OpeningBalance { get; init; }

    [Required]
    public DateOnly? OpeningDate { get; init; }
}

public sealed class AccountListRequest
{
    public int Page { get; init; } = 1;

    public int PageSize { get; init; } = 20;

    public bool? IsActive { get; init; }

    public string? Type { get; init; }

    public string Sort { get; init; } = "name";

    public string Direction { get; init; } = "asc";
}
