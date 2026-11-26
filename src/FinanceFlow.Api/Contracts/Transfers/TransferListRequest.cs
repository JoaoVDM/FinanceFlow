using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;

namespace FinanceFlow.Api.Contracts.Transfers;

public sealed class TransferListRequest
{
    [Range(1, int.MaxValue), FromQuery(Name = "page")]
    public int Page { get; init; } = 1;
    [Range(1, 100), FromQuery(Name = "pageSize")]
    public int PageSize { get; init; } = 20;
    [FromQuery(Name = "accountId")]
    public Guid? AccountId { get; init; }
    [FromQuery(Name = "from")]
    public DateOnly? From { get; init; }
    [FromQuery(Name = "to")]
    public DateOnly? To { get; init; }
    [FromQuery(Name = "sort")]
    public string Sort { get; init; } = "occurredOn";
    [FromQuery(Name = "direction")]
    public string Direction { get; init; } = "desc";
}
