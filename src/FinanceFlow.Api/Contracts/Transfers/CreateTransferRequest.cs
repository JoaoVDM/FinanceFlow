using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using FinanceFlow.Api.Contracts.Accounts;

namespace FinanceFlow.Api.Contracts.Transfers;

public sealed class CreateTransferRequest
{
    [Required]
    public Guid? SourceAccountId { get; init; }
    [Required]
    public Guid? DestinationAccountId { get; init; }
    [Required, JsonConverter(typeof(ExactMoneyJsonConverter))]
    public decimal? Amount { get; init; }
    [Required]
    public DateOnly? OccurredOn { get; init; }
    [StringLength(500)]
    public string? Description { get; init; }
}
