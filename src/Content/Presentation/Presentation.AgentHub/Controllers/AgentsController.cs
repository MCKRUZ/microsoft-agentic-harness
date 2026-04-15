using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Presentation.AgentHub.Controllers;

/// <summary>
/// REST controller for agent resource management.
/// Stub — conversation store integration added in section 03.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class AgentsController : ControllerBase
{
    /// <summary>Returns the list of available agents. Requires authentication.</summary>
    [HttpGet]
    public IActionResult Get() => Ok(Array.Empty<object>());
}
