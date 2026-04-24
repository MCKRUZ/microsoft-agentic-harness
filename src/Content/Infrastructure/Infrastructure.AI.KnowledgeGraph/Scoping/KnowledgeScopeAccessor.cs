using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.KnowledgeGraph;
using Domain.Common.Config;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.KnowledgeGraph.Scoping;

/// <summary>
/// Scoped <see cref="IKnowledgeScope"/> implementation that composes agent identity
/// from <see cref="IAgentExecutionContext"/> with knowledge-specific scope properties
/// (user, tenant, dataset) from configuration defaults.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="AgentId"/> and <see cref="ConversationId"/> are delegated from
/// <see cref="IAgentExecutionContext"/>. Tenant and dataset properties default to
/// <c>GraphRagConfig.DefaultTenantId</c> / <c>GraphRagConfig.DefaultDatasetId</c>
/// and can be overridden via <see cref="SetScope"/>.
/// </para>
/// <para>
/// Registered as <c>Scoped</c> so each request gets its own scope instance.
/// </para>
/// </remarks>
public sealed class KnowledgeScopeAccessor : IKnowledgeScope
{
    private readonly IAgentExecutionContext _agentContext;
    private readonly IOptionsMonitor<AppConfig> _configMonitor;

    private string? _userId;
    private string? _tenantId;
    private string? _datasetId;
    private string? _datasetName;
    private string? _datasetOwnerId;

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
    public string? UserId => _userId;

    /// <inheritdoc />
    public string? TenantId =>
        _tenantId ?? _configMonitor.CurrentValue.AI.Rag.GraphRag.DefaultTenantId;

    /// <inheritdoc />
    public string? DatasetId =>
        _datasetId ?? _configMonitor.CurrentValue.AI.Rag.GraphRag.DefaultDatasetId;

    /// <inheritdoc />
    public string? DatasetName => _datasetName;

    /// <inheritdoc />
    public string? DatasetOwnerId => _datasetOwnerId;

    /// <inheritdoc />
    public string? AgentId => _agentContext.AgentId;

    /// <inheritdoc />
    public string? ConversationId => _agentContext.ConversationId;

    /// <summary>
    /// Sets the knowledge scope properties for this request. Call once per request,
    /// typically from middleware or a pipeline behavior.
    /// </summary>
    /// <param name="userId">The authenticated user ID.</param>
    /// <param name="tenantId">The tenant ID (overrides config default).</param>
    /// <param name="datasetId">The dataset ID (overrides config default).</param>
    /// <param name="datasetName">The dataset display name.</param>
    /// <param name="datasetOwnerId">The dataset owner's user ID.</param>
    public void SetScope(
        string? userId = null,
        string? tenantId = null,
        string? datasetId = null,
        string? datasetName = null,
        string? datasetOwnerId = null)
    {
        _userId = userId;
        _tenantId = tenantId;
        _datasetId = datasetId;
        _datasetName = datasetName;
        _datasetOwnerId = datasetOwnerId;
    }
}
