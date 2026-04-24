namespace Application.AI.Common.Interfaces.KnowledgeGraph;

/// <summary>
/// Provides ambient scope information for knowledge graph operations, identifying
/// which user, tenant, and dataset an operation applies to. Used for multi-tenant
/// isolation, audit trails, and per-user knowledge partitioning.
/// </summary>
/// <remarks>
/// <para>
/// This is a separate interface from <see cref="Agent.IAgentExecutionContext"/> by design.
/// <c>IAgentExecutionContext</c> has 3 focused properties for agent identity propagation;
/// adding tenant/user/dataset fields would violate Interface Segregation. <c>IKnowledgeScope</c>
/// composes from <c>IAgentExecutionContext</c> internally, delegating <see cref="AgentId"/>
/// and <see cref="ConversationId"/> while adding knowledge-specific scope properties.
/// </para>
/// <para>
/// Implementations should be registered with <c>Scoped</c> lifetime (per-request) so that
/// scope properties are set once per request and remain consistent throughout the operation.
/// The scope is typically populated from JWT claims, HTTP headers, or configuration defaults.
/// </para>
/// </remarks>
public interface IKnowledgeScope
{
    /// <summary>
    /// The authenticated user performing the knowledge graph operation.
    /// Null when operating in system/anonymous context.
    /// </summary>
    string? UserId { get; }

    /// <summary>
    /// The tenant or organization the user belongs to. Null when multi-tenant
    /// isolation is disabled (single-tenant deployment).
    /// </summary>
    string? TenantId { get; }

    /// <summary>
    /// The unique identifier of the dataset being accessed. Null when using
    /// the user's default dataset.
    /// </summary>
    string? DatasetId { get; }

    /// <summary>
    /// A human-readable name for the dataset. Used for display and discovery.
    /// </summary>
    string? DatasetName { get; }

    /// <summary>
    /// The user ID of the dataset owner/creator. May differ from <see cref="UserId"/>
    /// when accessing a shared dataset.
    /// </summary>
    string? DatasetOwnerId { get; }

    /// <summary>
    /// The agent performing the operation, delegated from <see cref="Agent.IAgentExecutionContext"/>.
    /// </summary>
    string? AgentId { get; }

    /// <summary>
    /// The conversation in which the operation occurs, delegated from
    /// <see cref="Agent.IAgentExecutionContext"/>.
    /// </summary>
    string? ConversationId { get; }
}
