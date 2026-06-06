using Application.AI.Common.Interfaces.Identity;
using Domain.AI.Identity;
using Domain.Common.Config;
using Domain.Common.Config.AI.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.Identity;

/// <summary>
/// Default <see cref="IAgentIdentityValidator"/> — consults a static per-agent
/// allowlist in <see cref="ToolAuthorizationConfig"/> and returns whether the
/// supplied <see cref="AgentIdentity"/> is permitted to invoke the named tool.
/// Fail-closed: missing policy is deny, missing identity-id is deny,
/// <see cref="AgentIdentityKind.Unspecified"/> is deny.
/// </summary>
/// <remarks>
/// <para>
/// Together with <c>IKnowledgeScopeValidator</c> (the human-caller scope check),
/// forms the two-axis RBAC the harness applies at every tool call: "is the
/// initiating user allowed to touch this tenant's data?" AND "is this agent
/// allowed this tool?". Both checks are independent and ANDed.
/// </para>
/// <para>
/// Every deny emits a structured log entry distinguishing "policy denied X" from
/// "no policy for X". The public boolean conflates them by design (callers don't
/// need to know why an action was rejected, only that it was), but operators
/// debugging RBAC drift do.
/// </para>
/// <para>
/// PR-1 ships a static-config implementation. The interface contract allows a
/// future PR to swap to a dynamic policy store without touching call sites.
/// </para>
/// </remarks>
public sealed class EntraAgentIdentityValidator : IAgentIdentityValidator
{
    private readonly IOptionsMonitor<AppConfig> _appConfig;
    private readonly ILogger<EntraAgentIdentityValidator> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="EntraAgentIdentityValidator"/> class.
    /// </summary>
    /// <param name="appConfig">Application configuration monitor.</param>
    /// <param name="logger">Logger for audit-relevant deny events.</param>
    public EntraAgentIdentityValidator(
        IOptionsMonitor<AppConfig> appConfig,
        ILogger<EntraAgentIdentityValidator> logger)
    {
        ArgumentNullException.ThrowIfNull(appConfig);
        ArgumentNullException.ThrowIfNull(logger);

        _appConfig = appConfig;
        _logger = logger;
    }

    /// <inheritdoc />
    public bool CanInvoke(AgentIdentity identity, string toolKey)
    {
        ArgumentNullException.ThrowIfNull(identity);

        if (string.IsNullOrWhiteSpace(toolKey))
        {
            _logger.LogWarning(
                "RBAC deny — toolKey is null or whitespace. AgentId={AgentId}, Kind={Kind}.",
                identity.Id, identity.Kind);
            return false;
        }

        if (identity.Kind == AgentIdentityKind.Unspecified)
        {
            _logger.LogWarning(
                "RBAC deny — identity has Unspecified kind. AgentId={AgentId}, Tool={ToolKey}.",
                identity.Id, toolKey);
            return false;
        }

        if (string.IsNullOrWhiteSpace(identity.Id))
        {
            _logger.LogWarning(
                "RBAC deny — identity has no Id. Kind={Kind}, Tool={ToolKey}.",
                identity.Kind, toolKey);
            return false;
        }

        var allowlists = _appConfig.CurrentValue.AI?.Identity?.ToolAuthorization?.AllowedToolsByAgentId;
        if (allowlists is null || allowlists.Count == 0)
        {
            _logger.LogWarning(
                "RBAC deny — no ToolAuthorization config registered. AgentId={AgentId}, Tool={ToolKey}.",
                identity.Id, toolKey);
            return false;
        }

        if (!allowlists.TryGetValue(identity.Id, out var allowed))
        {
            _logger.LogInformation(
                "RBAC deny — no policy for agent. AgentId={AgentId}, Tool={ToolKey}.",
                identity.Id, toolKey);
            return false;
        }

        if (allowed.Count == 0)
        {
            _logger.LogInformation(
                "RBAC deny — agent's allowlist is empty. AgentId={AgentId}, Tool={ToolKey}.",
                identity.Id, toolKey);
            return false;
        }

        // Wildcard short-circuits before per-tool match so an agent with "*" is allowed
        // even tools added after the config was written.
        if (allowed.Contains(ToolAuthorizationConfig.WildcardToken, StringComparer.Ordinal))
            return true;

        if (allowed.Contains(toolKey, StringComparer.Ordinal))
            return true;

        _logger.LogInformation(
            "RBAC deny — tool not in agent's allowlist. AgentId={AgentId}, Tool={ToolKey}.",
            identity.Id, toolKey);
        return false;
    }
}
