using Microsoft.AspNetCore.Mvc;

namespace FinanceFlow.Api.Controllers;

/// <summary>Process liveness; no database or external dependency checks.</summary>
[ApiController]
[Route("health")]
public sealed class HealthController : ControllerBase
{
    [HttpGet]
    [HttpGet("live")]
    [ProducesResponseType<HealthResponse>(StatusCodes.Status200OK)]
    public ActionResult<HealthResponse> Get() => Ok(new HealthResponse("healthy"));
}

public sealed record HealthResponse(string Status);
