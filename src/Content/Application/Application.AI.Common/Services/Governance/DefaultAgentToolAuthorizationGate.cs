using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.Identity;
using Domain.AI.Governance;
using Domain.AI.Identity;
using Domain.Common.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Application.AI.Common.Services.Governance;

/// <summary>
/// Default <see cref="IAgentToolAuthorizationGate"/>: switched off unless
/// <c>AppConfig.AI.Identity.ToolAuthorization.Enabled</c> is set, and otherwise defers the
/// policy question to <see cref="IAgentIdentityValidator"/>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Where the executing identity comes from, and why it is not simply read off the
/// execution context.</strong> The context carries an identity only on the agent-turn path:
/// the resolution behaviour stamps it for <c>IAgentScopedRequest</c>, and the plan engine's
/// step executors and the Execution API's direct invoker each open a <em>fresh</em> DI scope,
/// whose context therefore starts blank. Reading the context alone would leave this gate
/// enforcing on one of the four execution paths the admission chain covers, and a control
/// that is skippable by issuing the same call from a plan step is not a control.
/// </para>
/// <para>
/// So the gate falls back to resolving the identity itself. That is sound rather than a
/// workaround because <see cref="AgentIdentity"/> is a <em>workload</em> identity — the
/// Entra-bound principal of the running agent, obtained from managed identity, a federated
/// credential, or a certificate — not a per-request or per-user value. Resolving it from a
/// child scope yields the same principal the parent turn would have carried; the resolver is
/// a singleton for exactly that reason. The result is cached for this scope's lifetime so a
/// plan step issuing many tool calls pays at most one acquisition.
/// </para>
/// <para>
/// <strong>Every path out of an enabled gate that is not a positive allow is a denial.</strong>
/// No validator registered, no identity resolvable, a resolver failure — each is a case where
/// the harness cannot establish who is asking. Treating any of them as permissive would
/// reproduce the defect this type exists to close, and matches the standing rule that an
/// unestablished scope is never a safe default.
/// </para>
/// </remarks>
public sealed class DefaultAgentToolAuthorizationGate : IAgentToolAuthorizationGate
{
    private readonly IOptionsMonitor<AppConfig> _appConfig;
    private readonly IAgentExecutionContext _executionContext;
    private readonly IAgentIdentityValidator? _validator;
    private readonly IAgentIdentityResolver? _resolver;
    private readonly ILogger<DefaultAgentToolAuthorizationGate> _logger;

    // Scoped service, so this caches for one turn / one plan step / one direct invocation.
    private AgentIdentity? _resolvedIdentity;
    private bool _resolutionAttempted;

    /// <summary>
    /// Initializes a new instance of the <see cref="DefaultAgentToolAuthorizationGate"/> class.
    /// </summary>
    /// <param name="appConfig">Supplies the feature switch and the per-agent allowlist.</param>
    /// <param name="executionContext">
    /// The current scope's execution context. Carries the identity already on the agent-turn
    /// path; blank on the plan and Execution API paths, which is why it is only the first place
    /// looked rather than the only one.
    /// </param>
    /// <param name="logger">Records why an enabled gate could not establish an identity.</param>
    /// <param name="validator">
    /// The policy oracle. Optional so that a host composing this layer without the Infrastructure
    /// identity registrations still starts; an enabled gate with no validator denies rather than
    /// admits, and <c>ToolAuthorizationConfigValidator</c> turns that combination into a startup
    /// failure so it is not first discovered as a refused tool call.
    /// </param>
    /// <param name="resolver">
    /// Supplies the workload identity when the execution context has none. Optional for the same
    /// reason as <paramref name="validator"/>, and with the same fail-closed consequence.
    /// </param>
    public DefaultAgentToolAuthorizationGate(
        IOptionsMonitor<AppConfig> appConfig,
        IAgentExecutionContext executionContext,
        ILogger<DefaultAgentToolAuthorizationGate> logger,
        IAgentIdentityValidator? validator = null,
        IAgentIdentityResolver? resolver = null)
    {
        ArgumentNullException.ThrowIfNull(appConfig);
        ArgumentNullException.ThrowIfNull(executionContext);
        ArgumentNullException.ThrowIfNull(logger);

        _appConfig = appConfig;
        _executionContext = executionContext;
        _logger = logger;
        _validator = validator;
        _resolver = resolver;
    }

    /// <inheritdoc />
    public async ValueTask<AgentToolAuthorizationVerdict> EvaluateAsync(
        string toolKey,
        CancellationToken cancellationToken)
    {
        var identityConfig = _appConfig.CurrentValue.AI?.Identity;
        if (identityConfig?.ToolAuthorization is not { Enabled: true })
            return AgentToolAuthorizationVerdict.Allow();

        if (_validator is null)
        {
            _logger.LogError(
                "Tool authorization is enabled but no IAgentIdentityValidator is registered; "
                + "refusing {ToolKey}. Register the Infrastructure identity services or turn "
                + "AI.Identity.ToolAuthorization.Enabled off.",
                toolKey);
            return Deny(toolKey);
        }

        var identity = await GetIdentityAsync(cancellationToken).ConfigureAwait(false);
        if (identity is null)
        {
            _logger.LogWarning(
                "Tool authorization is enabled but no agent identity could be established; "
                + "refusing {ToolKey}.",
                toolKey);
            return Deny(toolKey);
        }

        return _validator.CanInvoke(identity, toolKey)
            ? AgentToolAuthorizationVerdict.Allow()
            : Deny(toolKey);
    }

    private async ValueTask<AgentIdentity?> GetIdentityAsync(CancellationToken cancellationToken)
    {
        // The context wins when it has one: on the agent-turn path the resolution behaviour has
        // already paid for acquisition, and re-resolving would both waste a round trip and risk
        // answering with a different principal than the rest of the turn is using.
        if (_executionContext.AgentIdentity is { } stamped)
            return stamped;

        if (_resolutionAttempted)
            return _resolvedIdentity;

        _resolutionAttempted = true;

        if (_resolver is null)
        {
            _logger.LogError(
                "Tool authorization is enabled but no IAgentIdentityResolver is registered, so an "
                + "identity cannot be established outside an agent turn.");
            return null;
        }

        var identityConfig = _appConfig.CurrentValue.AI!.Identity!;
        var credentialContext = new CredentialContext
        {
            Audience = identityConfig.DefaultAudience,
            Scopes = [.. identityConfig.DefaultScopes]
        };

        var resolution = await _resolver
            .ResolveAsync(credentialContext, cancellationToken)
            .ConfigureAwait(false);

        if (!resolution.IsSuccess || resolution.Value is null)
        {
            // Stable codes only. A credential provider's exception text can carry a token, a SAS
            // query string, or a certificate path; the provider is responsible for logging the
            // detail, and this records only that acquisition failed and why in scrubbed terms.
            _logger.LogError(
                "Agent identity resolution failed while authorizing a tool call: {Errors}",
                resolution.Errors.Count == 0 ? "no error details" : string.Join("; ", resolution.Errors));
            return null;
        }

        _resolvedIdentity = resolution.Value;
        return _resolvedIdentity;
    }

    // Routed through the shared factory so this stage's refusal is textually identical to every
    // other gate's: a denied caller must not be able to infer which gate fired.
    private static AgentToolAuthorizationVerdict Deny(string toolKey) =>
        AgentToolAuthorizationVerdict.Deny(GovernanceDenials.NotPermitted(toolKey));
}
