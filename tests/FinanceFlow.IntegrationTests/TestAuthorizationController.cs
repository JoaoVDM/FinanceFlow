using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FinanceFlow.IntegrationTests;

[ApiController, Authorize, Route("api/v1/__tests/authorization")]
public sealed class TestAuthorizationController : ControllerBase
{
    [HttpGet, Authorize(Policy = "test-deny")]
    public IActionResult Get() => Ok();
}
