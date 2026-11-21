using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using FinanceFlow.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FinanceFlow.Api.Controllers;

/// <summary>Process liveness; no database or external dependency checks.</summary>
[ApiController]
[AllowAnonymous]
[Route("health")]
public sealed class HealthController : ControllerBase
{
    [HttpGet]
    [HttpGet("live")]
    [ProducesResponseType<HealthResponse>(StatusCodes.Status200OK)]
    public ActionResult<HealthResponse> Get() => Ok(new HealthResponse("healthy"));

    [HttpGet("ready")]
    [ProducesResponseType<HealthResponse>(200), ProducesResponseType<HealthResponse>(503)]
    public async Task<IActionResult> Ready([FromServices] FinanceFlowDbContext db, CancellationToken cancellationToken)
    {
        try
        {
            if (!await db.Database.CanConnectAsync(cancellationToken)
                || (await db.Database.GetPendingMigrationsAsync(cancellationToken)).Any())
                return StatusCode(503, new HealthResponse("notReady"));
            // Verify required runtime columns as well as the migration history.
            await db.Users.AsNoTracking().OrderBy(x => x.Id)
                .Select(x => new { x.Id, x.NormalizedEmail, x.IsActive, x.TokenVersion })
                .Take(1).ToListAsync(cancellationToken);
            // Readiness also requires access to the new financial objects, including an empty database.
            await db.FinancialAccounts.AsNoTracking().OrderBy(x => x.Id).Select(x => x.Id).Take(1).ToListAsync(cancellationToken);
            await db.IdempotencyRecords.AsNoTracking().OrderBy(x => x.Id).Select(x => x.Id).Take(1).ToListAsync(cancellationToken);
            await db.Categories.AsNoTracking().OrderBy(category => category.Id)
                .Select(category => category.Id).Take(1).ToListAsync(cancellationToken);
            await db.FinancialTransactions.AsNoTracking().OrderBy(transaction => transaction.Id)
                .Select(transaction => transaction.Id).Take(1).ToListAsync(cancellationToken);
            await db.Transfers.AsNoTracking().OrderBy(transfer => transfer.Id)
                .Select(transfer => transfer.Id).Take(1).ToListAsync(cancellationToken);
            return Ok(new HealthResponse("ready"));
        }
        catch (Exception exception) when (exception is System.Data.Common.DbException or InvalidOperationException or TimeoutException)
        {
            return StatusCode(503, new HealthResponse("notReady"));
        }
    }
}

public sealed record HealthResponse(string Status);
