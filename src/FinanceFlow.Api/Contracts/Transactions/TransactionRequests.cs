using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using FinanceFlow.Api.Contracts.Accounts;
using Microsoft.AspNetCore.Mvc;

namespace FinanceFlow.Api.Contracts.Transactions;

public sealed class CreateTransactionRequest
{
    [Required]
    public Guid? AccountId { get; init; }
    [Required]
    public Guid? CategoryId { get; init; }
    [Required]
    public string Kind { get; init; } = string.Empty;
    [Required, JsonConverter(typeof(ExactMoneyJsonConverter))]
    public decimal? Amount { get; init; }
    [Required]
    public DateOnly? OccurredOn { get; init; }
    [StringLength(500)]
    public string? Description { get; init; }
}

public sealed class UpdateTransactionRequest
{
    [Required]
    public Guid? AccountId { get; init; }
    [Required]
    public Guid? CategoryId { get; init; }
    [Required]
    public string Kind { get; init; } = string.Empty;
    [Required, JsonConverter(typeof(ExactMoneyJsonConverter))]
    public decimal? Amount { get; init; }
    [Required]
    public DateOnly? OccurredOn { get; init; }
    [StringLength(500)]
    public string? Description { get; init; }
}

public sealed class TransactionListRequest
{
    [Range(1, int.MaxValue), FromQuery(Name = "page")]
    public int Page { get; init; } = 1;
    [Range(1, 100), FromQuery(Name = "pageSize")]
    public int PageSize { get; init; } = 20;
    [FromQuery(Name = "accountId")]
    public Guid? AccountId { get; init; }
    [FromQuery(Name = "categoryId")]
    public Guid? CategoryId { get; init; }
    [FromQuery(Name = "kind")]
    public string? Kind { get; init; }
    [FromQuery(Name = "from")]
    public DateOnly? From { get; init; }
    [FromQuery(Name = "to")]
    public DateOnly? To { get; init; }
    // Parse the original tokens with the same exact-money converter used for JSON bodies.
    [FromQuery(Name = "minAmount")]
    public string? MinAmount { get; init; }
    [FromQuery(Name = "maxAmount")]
    public string? MaxAmount { get; init; }
    [FromQuery(Name = "sort")]
    public string Sort { get; init; } = "occurredOn";
    [FromQuery(Name = "direction")]
    public string Direction { get; init; } = "desc";
}
