using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.Egress;
using Domain.AI.Egress;
using Domain.AI.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Egress;

/// <summary>
/// Outer <see cref="DelegatingHandler"/> in the egress chain. Resolves the
/// per-identity <see cref="IEgressPolicy"/> from the ambient request scope,
/// asks it for a verdict on every outbound <see cref="HttpRequestMessage"/>,
/// writes the verdict to the JSONL audit, and throws
/// <see cref="EgressBlockedException"/> on deny. Pass-through on allow
/// delegates to the inner handler — which, in the registered named client, is
/// the <c>AntiSSRFHandler</c> that performs connect-time IP filtering.
/// </summary>
/// <remarks>
/// <para>
/// The handler is the OUTER ring of egress defense. It enforces the
/// declarative hostname allowlist before any DNS resolution happens, and emits
/// structured audit + logging on every decision regardless of verdict. The
/// inner ring (AntiSSRF) closes the DNS-rebinding window by resolving and
/// filtering at the socket-connect boundary. Both must pass for a request to
/// leave the process; failing either ring blocks it.
/// </para>
/// <para>
/// Identity flows in via <see cref="IAmbientRequestScope.Current"/>. When no
/// request scope is active (e.g. background work outside an agent turn) the
/// handler short-circuits to deny — the policy layer refuses to make a verdict
/// without an attributable identity, because the audit trail and per-skill
/// allowlists are meaningless otherwise. Consumers who need to make outbound
/// calls outside an agent scope must use a different <see cref="HttpClient"/>
/// or establish an explicit "system" agent identity.
/// </para>
/// </remarks>
public sealed class EgressPolicyDelegatingHandler : DelegatingHandler
{
    /// <summary>The well-known name of the registered <see cref="HttpClient"/> that composes this handler above the SSRF defense.</summary>
    public const string ClientName = "egress";

    private readonly IServiceProvider _rootServices;
    private readonly IAmbientRequestScope _ambientScope;
    private readonly IEgressAuditWriter _auditWriter;
    private readonly ILogger<EgressPolicyDelegatingHandler> _logger;
    private readonly TimeProvider _timeProvider;

    /// <summary>Initializes a new <see cref="EgressPolicyDelegatingHandler"/>.</summary>
    public EgressPolicyDelegatingHandler(
        IServiceProvider rootServices,
        IAmbientRequestScope ambientScope,
        IEgressAuditWriter auditWriter,
        ILogger<EgressPolicyDelegatingHandler> logger,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(rootServices);
        ArgumentNullException.ThrowIfNull(ambientScope);
        ArgumentNullException.ThrowIfNull(auditWriter);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _rootServices = rootServices;
        _ambientScope = ambientScope;
        _auditWriter = auditWriter;
        _logger = logger;
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var target = request.RequestUri
            ?? throw new InvalidOperationException("Outbound HTTP request has no RequestUri.");

        var identity = ResolveIdentity();
        if (identity is null)
        {
            var decision = new EgressDecision
            {
                Allowed = false,
                Reason = "No agent identity bound to the request scope; egress requires an attributable identity.",
                Target = target,
                DecidedAt = _timeProvider.GetUtcNow()
            };

            // Best-effort audit: synthesize a placeholder identity row so the
            // line still carries SOME attribution.
            await _auditWriter.AppendAsync(decision, UnattributedIdentity, cancellationToken).ConfigureAwait(false);
            _logger.LogWarning(
                "Egress to '{Host}' blocked: no agent identity in scope.",
                target.Host);
            throw new EgressBlockedException(decision);
        }

        var policy = ResolvePolicy(identity);
        var verdict = await policy.AllowAsync(target, identity, cancellationToken).ConfigureAwait(false);

        await _auditWriter.AppendAsync(verdict, identity, cancellationToken).ConfigureAwait(false);

        if (!verdict.Allowed)
        {
            _logger.LogWarning(
                "Egress to '{Host}' blocked for identity '{Agent}': {Reason}",
                target.Host,
                identity.Id,
                verdict.Reason);
            throw new EgressBlockedException(verdict);
        }

        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private AgentIdentity? ResolveIdentity()
    {
        var requestServices = _ambientScope.Current;
        if (requestServices is null)
        {
            return null;
        }

        var context = requestServices.GetService<IAgentExecutionContext>();
        return context?.AgentIdentity;
    }

    private IEgressPolicy ResolvePolicy(AgentIdentity identity)
    {
        // Resolver is registered as a singleton in the root container — prefer
        // the root provider so the lookup succeeds even when the ambient
        // request scope is a narrow fake (test fixtures) or a scoped provider
        // that doesn't carry the singleton.
        var resolver = _rootServices.GetService<IEgressPolicyResolver>()
            ?? _ambientScope.Current?.GetService<IEgressPolicyResolver>()
            ?? throw new InvalidOperationException(
                "No IEgressPolicyResolver registered. Call services.RegisterEgressServices() during DI composition.");
        return resolver.ResolveFor(identity);
    }

    private static readonly AgentIdentity UnattributedIdentity = new()
    {
        Id = "unattributed",
        Kind = AgentIdentityKind.Unspecified
    };
}
