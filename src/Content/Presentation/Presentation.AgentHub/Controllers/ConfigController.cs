using Domain.Common.Config;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Presentation.AgentHub.Controllers;

/// <summary>
/// Read-only configuration endpoints consumed by the WebUI to populate settings surfaces
/// (deployment pickers, etc.). All endpoints require authentication; no secrets are ever
/// surfaced — only the authoritative list of allowed values.
/// </summary>
[ApiController]
[Route("api/config")]
[Authorize]
public sealed class ConfigController : ControllerBase
{
    private readonly IOptionsMonitor<AppConfig> _appConfig;

    /// <summary>Initialises the controller with its dependencies.</summary>
    public ConfigController(IOptionsMonitor<AppConfig> appConfig)
    {
        _appConfig = appConfig;
    }

    /// <summary>
    /// Returns the authoritative list of deployment/model names a caller may request as a
    /// per-conversation override, along with the system default. When
    /// <c>AgentFrameworkConfig.AvailableDeployments</c> is empty the response falls back to
    /// a single-entry list containing only <see cref="DeploymentsResponse.DefaultDeployment"/>.
    /// </summary>
    [HttpGet("deployments")]
    public ActionResult<DeploymentsResponse> GetDeployments()
    {
        var framework = _appConfig.CurrentValue.AI?.AgentFramework;
        var defaultDeployment = framework?.DefaultDeployment ?? "default";
        var deployments = framework?.AvailableDeployments is { Count: > 0 } list
            ? list.ToArray()
            : new[] { defaultDeployment };

        return Ok(new DeploymentsResponse(deployments, defaultDeployment));
    }
}

/// <summary>
/// Response payload for <c>GET /api/config/deployments</c>.
/// </summary>
/// <param name="Deployments">The authoritative set of deployment names a client may request.</param>
/// <param name="DefaultDeployment">The deployment used when the caller does not specify an override.</param>
public sealed record DeploymentsResponse(
    IReadOnlyList<string> Deployments,
    string DefaultDeployment);
