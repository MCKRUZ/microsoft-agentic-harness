using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.KnowledgeGraph;
using Domain.Common.Config;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.KnowledgeGraph.Scoping;

/// <summary>
/// <see cref="IKnowledgeScope"/> implementation that composes agent identity from
/// <see cref="IAgentExecutionContext"/> with <em>ambient</em> knowledge-specific scope
/// properties (user, tenant, dataset) established at the request entry point.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="AgentId"/> and <see cref="ConversationId"/> are delegated from the scoped
/// <see cref="IAgentExecutionContext"/> — so a sub-agent running in a child scope correctly
/// reports its own agent/conversation. User/tenant/dataset, by contrast, are stored in an
/// <see cref="AsyncLocal{T}"/> set once by <see cref="SetScope"/> at the entry point
/// (<c>KnowledgeScopeMiddleware</c> / <c>KnowledgeScopeHubFilter</c>).
/// </para>
/// <para>
/// Ambient (rather than per-instance) storage is deliberate: the orchestrator and the DAG plan
/// executor run sub-agents in fresh child DI scopes, and the conversation-to-knowledge write runs
/// on a post-turn background task after the request scope is disposed. An <see cref="AsyncLocal{T}"/>
/// flows the human caller's identity into all of those execution contexts, so memory written/recalled
/// from a sub-agent or a background continuation is attributed to the right user/tenant instead of
/// silently falling back to the shared default namespace. This mirrors the
/// <c>IAmbientRequestScope</c> bridge used for the same caching/child-scope reasons.
/// </para>
/// <para>
/// Tenant and dataset fall back to <c>GraphRagConfig.DefaultTenantId</c> /
/// <c>GraphRagConfig.DefaultDatasetId</c> when the ambient scope is unset (single-tenant deployment,
/// anonymous request, or background work outside any request).
/// </para>
/// </remarks>
public sealed class KnowledgeScopeAccessor : IKnowledgeScope, IKnowledgeScopeWriter
{
    private static readonly AsyncLocal<ScopeIdentity?> s_identity = new();

    private readonly IAgentExecutionContext _agentContext;
    private readonly IOptionsMonitor<AppConfig> _configMonitor;

    /// <summary>
    /// Initializes a new instance of the <see cref="KnowledgeScopeAccessor"/> class.
    /// </summary>
    /// <param name="agentContext">The agent execution context for identity delegation.</param>
    /// <param name="configMonitor">Application configuration for default scope values.</param>
    public KnowledgeScopeAccessor(
        IAgentExecutionContext agentContext,
        IOptionsMonitor<AppConfig> configMonitor)
    {
        ArgumentNullException.ThrowIfNull(agentContext);
        ArgumentNullException.ThrowIfNull(configMonitor);

        _agentContext = agentContext;
        _configMonitor = configMonitor;
    }

    /// <inheritdoc />
    public string? UserId => s_identity.Value?.UserId;

    /// <inheritdoc />
    public string? TenantId =>
        s_identity.Value?.TenantId ?? _configMonitor.CurrentValue.AI.Rag.GraphRag.DefaultTenantId;

    /// <inheritdoc />
    public string? DatasetId =>
        s_identity.Value?.DatasetId ?? _configMonitor.CurrentValue.AI.Rag.GraphRag.DefaultDatasetId;

    /// <inheritdoc />
    public string? DatasetName => s_identity.Value?.DatasetName;

    /// <inheritdoc />
    public string? DatasetOwnerId => s_identity.Value?.DatasetOwnerId;

    /// <inheritdoc />
    public string? AgentId => _agentContext.AgentId;

    /// <inheritdoc />
    public string? ConversationId => _agentContext.ConversationId;

    /// <summary>
    /// Establishes the ambient knowledge scope for the current async execution context. Call once per
    /// request from authenticated entry-point middleware or a hub filter; the value flows to child
    /// scopes and background continuations of that request.
    /// </summary>
    /// <param name="userId">The authenticated user ID.</param>
    /// <param name="tenantId">The tenant ID (overrides config default).</param>
    /// <param name="datasetId">The dataset ID (overrides config default).</param>
    /// <param name="datasetName">The dataset display name.</param>
    /// <param name="datasetOwnerId">The dataset owner's user ID.</param>
    /// <returns>A token that restores the previously ambient identity when disposed.</returns>
    /// <remarks>
    /// The token captures and restores the <em>previous</em> value rather than clearing to null, which
    /// is what makes nested scopes (A → B → dispose → back to A) behave. A long-lived
    /// <c>BackgroundService</c> that drains a job queue must dispose per job; otherwise job N's identity
    /// stays ambient in the loop's execution context and job N+1 silently runs as job N's user.
    /// </remarks>
    public IDisposable SetScope(
        string? userId = null,
        string? tenantId = null,
        string? datasetId = null,
        string? datasetName = null,
        string? datasetOwnerId = null)
    {
        var previous = s_identity.Value;
        s_identity.Value = new ScopeIdentity(userId, tenantId, datasetId, datasetName, datasetOwnerId);
        return new ScopeToken(previous);
    }

    private sealed record ScopeIdentity(
        string? UserId,
        string? TenantId,
        string? DatasetId,
        string? DatasetName,
        string? DatasetOwnerId);

    /// <summary>
    /// Restore token mirroring <c>AmbientRequestScope.ScopeToken</c>: reinstates the identity that was
    /// ambient before the corresponding <see cref="SetScope"/> call. Idempotent — a second dispose is a
    /// no-op rather than a second (wrong) restore.
    /// </summary>
    private sealed class ScopeToken : IDisposable
    {
        private readonly ScopeIdentity? _previous;
        private bool _disposed;

        public ScopeToken(ScopeIdentity? previous) => _previous = previous;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            s_identity.Value = _previous;
        }
    }
}
