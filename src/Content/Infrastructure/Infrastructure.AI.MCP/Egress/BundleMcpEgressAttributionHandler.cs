using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Agent;
using Domain.AI.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Infrastructure.AI.MCP.Egress;

/// <summary>
/// Outermost handler in a bundle-owned MCP server's HTTP pipeline. Establishes a fresh, self-contained
/// agent identity for the duration of exactly one outbound request, then delegates down to the shared
/// egress-policy and SSRF rings — so every request over a bundle-owned connection is attributed and
/// audited, regardless of whatever ambient context (or lack of one) the calling code happens to have.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this exists.</strong> <c>EgressPolicyDelegatingHandler</c> (the shared allowlist + audit
/// ring every other outbound path in the harness goes through) refuses to act without an agent identity
/// pulled from <see cref="IAmbientRequestScope.Current"/> — by design, an unattributable request is
/// denied rather than silently permitted. A bundle-owned MCP connection is contacted from two places
/// with two different ambient states: <c>BundleRunExecutor</c>'s tool-discovery pass, which runs before
/// any DI scope exists at all, and <c>ToolChainBuilder</c>'s later tool resolution, which runs inside a
/// real request scope but only carries a resolved identity when the separate, opt-in identity subsystem
/// (<c>AppConfig.AI.Identity.Enabled</c>) happens to be on. Relying on whatever identity is ambient at
/// each call site would make the connection's behaviour depend on unrelated configuration and calling
/// context; instead, this handler gives the connection its own identity, asserted fresh on every single
/// request, so the egress ring always has something real to evaluate — the bundle itself.
/// </para>
/// <para>
/// This does not weaken attribution: the identity used here (<see cref="AgentIdentityKind.System"/>,
/// <c>Id</c> = the bundle-scoped server name) is never a real Entra principal and is scoped to exactly
/// the one bundle whose server this client was built for — see
/// <see cref="Infrastructure.AI.MCP.Services.McpConnectionManager"/>, which constructs one of these per
/// bundle-owned server name, never shared across bundles.
/// </para>
/// </remarks>
public sealed class BundleMcpEgressAttributionHandler : DelegatingHandler
{
    private readonly string _attributionId;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IAmbientRequestScope _ambientScope;

    /// <summary>
    /// Initializes a new <see cref="BundleMcpEgressAttributionHandler"/> attributing every request it
    /// sends to <paramref name="attributionId"/>.
    /// </summary>
    /// <param name="attributionId">
    /// The identity id every request through this handler is attributed to — the bundle-scoped,
    /// namespaced MCP server name, so the audit trail and any allowlist decision can be traced back to
    /// exactly which bundle's server made the call.
    /// </param>
    /// <param name="scopeFactory">Used to create a short-lived DI scope per request.</param>
    /// <param name="ambientScope">The ambient request-scope holder <c>EgressPolicyDelegatingHandler</c> reads identity from.</param>
    public BundleMcpEgressAttributionHandler(
        string attributionId, IServiceScopeFactory scopeFactory, IAmbientRequestScope ambientScope)
    {
        ArgumentException.ThrowIfNullOrEmpty(attributionId);
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(ambientScope);

        _attributionId = attributionId;
        _scopeFactory = scopeFactory;
        _ambientScope = ambientScope;
    }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // A fresh scope per request, not one captured once at construction: this handler instance is
        // cached and reused for the connection's entire lifetime (McpConnectionManager.ResolveBundleEgressClient),
        // and HttpClient.SendAsync is explicitly safe to call concurrently — so two overlapping requests
        // over the SAME cached client (e.g. two simultaneous tool calls against one bundle-owned server)
        // must each get their own scope. IAmbientRequestScope.BeginScope pushes onto an AsyncLocal, which
        // is per-async-flow, not shared state a single push could cover for every future caller: pushing
        // once at construction would only affect whichever flow happened to build the handler, leaving
        // every other concurrent flow's ambient identity untouched (or, if a naively shared scope were
        // reused, IAgentExecutionContext.SetIdentity would throw on the second call within it). The scope
        // is cheap and this handler is not on a hot per-token path — it fires once per MCP request, not
        // per model token.
        await using var scope = _scopeFactory.CreateAsyncScope();

        var executionContext = scope.ServiceProvider.GetRequiredService<IAgentExecutionContext>();
        executionContext.SetIdentity(new AgentIdentity { Id = _attributionId, Kind = AgentIdentityKind.System });

        // Overwrites whatever ambient scope (if any) the caller had for the lifetime of this one
        // request, then restores it on disposal (IAmbientRequestScope.BeginScope's documented
        // contract) — so this request is always attributed to the bundle, never to whatever else is
        // running around it, and the caller's own context is never disturbed beyond this one call.
        using var _ = _ambientScope.BeginScope(scope.ServiceProvider);

        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
