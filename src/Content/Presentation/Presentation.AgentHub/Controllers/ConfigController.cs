using Application.AI.Common.Interfaces;
using Domain.Common.Config;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Presentation.AgentHub.Controllers;

/// <summary>
/// Read-only configuration endpoints consumed by the WebUI to populate settings surfaces
/// (deployment pickers, etc.). Most endpoints require authentication; no secrets are ever
/// surfaced — only the authoritative list of allowed values.
/// </summary>
[ApiController]
[Route("api/config")]
[Authorize]
public sealed class ConfigController : ControllerBase
{
    private readonly IOptionsMonitor<AppConfig> _appConfig;
    private readonly IChatClientFactory _chatClientFactory;
    private readonly bool _authDisabled;

    /// <summary>Initialises the controller with its dependencies.</summary>
    public ConfigController(
        IOptionsMonitor<AppConfig> appConfig,
        IChatClientFactory chatClientFactory,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        _appConfig = appConfig;
        _chatClientFactory = chatClientFactory;
        _authDisabled = Auth.AuthBypassPolicy.IsBypassed(environment, configuration);
    }

    /// <summary>
    /// Returns the server's authentication mode so the WebUI can detect client/server
    /// auth mismatches at connection time rather than failing silently.
    /// Anonymous: must be callable before authentication is established.
    /// </summary>
    [AllowAnonymous]
    [HttpGet("auth")]
    public ActionResult GetAuthMode() =>
        Ok(new { authDisabled = _authDisabled });

    /// <summary>
    /// Reports whether the active AI provider is configured to serve agent turns, and — when it is
    /// not — the names of the settings that are missing. Lets the WebUI show an actionable banner
    /// instead of letting requests fail with an opaque error. Returns only setting names and
    /// booleans; never secret values.
    /// Anonymous (like <see cref="GetAuthMode"/>): the banner must be able to warn even when auth
    /// is itself half-configured, and the payload exposes no secrets.
    /// </summary>
    [AllowAnonymous]
    [HttpGet("status")]
    public ActionResult<AiProviderStatusResponse> GetStatus()
    {
        var status = _chatClientFactory.GetProviderStatus();
        return Ok(new AiProviderStatusResponse(
            status.IsConfigured,
            status.ClientType.ToString(),
            status.DefaultDeployment,
            status.MissingSettings));
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

/// <summary>
/// Response payload for <c>GET /api/config/status</c>. Names and booleans only — never secrets.
/// </summary>
/// <param name="Configured">Whether the active AI provider can serve agent turns.</param>
/// <param name="ClientType">The active client type (e.g. <c>Anthropic</c>, <c>AzureOpenAI</c>).</param>
/// <param name="DefaultDeployment">The configured default deployment/model name.</param>
/// <param name="MissingSettings">Configuration keys to supply when <paramref name="Configured"/> is false.</param>
public sealed record AiProviderStatusResponse(
    bool Configured,
    string ClientType,
    string DefaultDeployment,
    IReadOnlyList<string> MissingSettings);
