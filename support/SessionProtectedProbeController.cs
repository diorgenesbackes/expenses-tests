using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Expenses.Test.Support;

[ApiController]
[Route("test/session-protected")]
[Authorize]
public sealed class SessionProtectedProbeController : ControllerBase
{
    [HttpGet] public IActionResult Get() => Ok(new { authenticated = true });
}
