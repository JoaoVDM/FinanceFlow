using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;

namespace FinanceFlow.IntegrationTests;

[ApiController]
[Route("api/v1/__tests")]
public sealed class TestOnlyController : ControllerBase
{
    [HttpGet("failure")]
    public IActionResult Failure() => throw new InvalidOperationException("sensitive-test-detail");

    [HttpPost("validation")]
    public IActionResult Validate(ValidationProbeRequest request) => Ok(request);
}

public sealed class ValidationProbeRequest
{
    [Required]
    public string? Name { get; init; }
}
