using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;

namespace FinanceFlow.Api.Contracts.Statements;

public sealed class StatementRequest
{
    [Required, FromQuery(Name = "from")]
    public DateOnly? From { get; init; }
    [Required, FromQuery(Name = "to")]
    public DateOnly? To { get; init; }
    [Range(1, int.MaxValue), FromQuery(Name = "page")]
    public int Page { get; init; } = 1;
    [Range(1, 100), FromQuery(Name = "pageSize")]
    public int PageSize { get; init; } = 20;
}
