using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;

namespace FinanceFlow.Api.Contracts.Reports;

public sealed class SummaryRequest
{
    [Required, FromQuery(Name = "from")]
    public DateOnly? From { get; init; }
    [Required, FromQuery(Name = "to")]
    public DateOnly? To { get; init; }
    [FromQuery(Name = "accountId")]
    public Guid? AccountId { get; init; }
}
