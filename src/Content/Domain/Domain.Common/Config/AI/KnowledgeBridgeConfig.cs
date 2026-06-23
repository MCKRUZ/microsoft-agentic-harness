namespace Domain.Common.Config.AI;

/// <summary>
/// Configuration for the Conversation-to-Knowledge Bridge.
/// Controls whether fact extraction runs, confidence thresholds, and timeout behavior.
/// Bound to <c>AppConfig:AI:KnowledgeBridge</c>.
/// </summary>
public sealed class KnowledgeBridgeConfig
{
    /// <summary>
    /// Master toggle for cross-session memory (both fact extraction via <c>KnowledgeExtractionBehavior</c>
    /// and recall via <c>KnowledgeMemoryContextProvider</c>). When false, both pass through with zero
    /// overhead — no LLM calls, no background tasks, no recalled context.
    /// </summary>
    /// <remarks>
    /// <strong>Opt-in by default (secure-by-default template posture).</strong> Memory is scoped per
    /// user/tenant: remembered facts are namespaced by the authenticated identity, which the host
    /// establishes automatically via <c>KnowledgeScopeMiddleware</c> (HTTP) and
    /// <c>KnowledgeScopeHubFilter</c> (SignalR) from the <c>oid</c>/<c>tid</c> claims. With that wiring
    /// in place it is safe to enable in a multi-user deployment — one user can no longer recall another's
    /// facts. It ships <see langword="false"/> so a cloned template never turns on a user-data feature
    /// implicitly; set it <see langword="true"/> deliberately once you have confirmed your auth provider
    /// supplies a stable user identity (and, for multi-tenant, a tenant claim).
    /// </remarks>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Minimum LLM confidence (0.0–1.0) required to persist an extracted fact.
    /// Facts below this threshold are silently discarded.
    /// </summary>
    public double MinConfidence { get; set; } = 0.7;

    /// <summary>
    /// Hard timeout in seconds for the background extraction LLM call.
    /// Prevents runaway requests from consuming resources indefinitely.
    /// </summary>
    public int ExtractionTimeoutSeconds { get; set; } = 10;

    /// <summary>
    /// Operation name passed to <c>IModelRouter.RouteOperationAsync</c> to resolve
    /// the extraction LLM client. Should map to the economy tier in
    /// <c>ModelRoutingConfig.OperationOverrides</c>.
    /// </summary>
    public string RoutingOperationName { get; set; } = "fact_extraction";

    /// <summary>
    /// The memory write gate — scans, classifies, and stamps provenance on facts before they enter
    /// long-term memory, and quarantines untrusted facts from recall. Enabled by default whenever
    /// memory itself is enabled (defense-by-default).
    /// </summary>
    public MemoryGuardConfig MemoryGuard { get; set; } = new();
}
