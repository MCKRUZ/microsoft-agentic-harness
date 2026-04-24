namespace Domain.AI.KnowledgeGraph.Models;

/// <summary>
/// Describes the ownership and access scope for knowledge graph entries, enabling
/// multi-tenant isolation where multiple agents or users share the same graph
/// infrastructure but operate on isolated datasets.
/// </summary>
/// <remarks>
/// <para>
/// Modeled after Cognee's <c>AgentScope</c> pattern (user → dataset → owner) but
/// extended with <see cref="TenantId"/> for enterprise multi-tenancy. The scope
/// hierarchy is: Tenant → User → Dataset, where each level narrows the visible
/// portion of the knowledge graph.
/// </para>
/// <para>
/// When <see cref="TenantId"/> is <c>null</c>, tenant isolation is disabled and all
/// users share a single namespace. When <see cref="DatasetId"/> is <c>null</c>, the
/// user's default dataset is used.
/// </para>
/// </remarks>
public record KnowledgeScopeDescriptor
{
    /// <summary>
    /// The authenticated user who owns or is accessing the knowledge graph entries.
    /// </summary>
    public required string UserId { get; init; }

    /// <summary>
    /// The tenant or organization that the user belongs to. Null when multi-tenant
    /// isolation is disabled.
    /// </summary>
    public string? TenantId { get; init; }

    /// <summary>
    /// A human-readable name for the dataset (e.g., "research_papers", "product_docs").
    /// Used for display and discovery; <see cref="DatasetId"/> is the lookup key.
    /// </summary>
    public string? DatasetName { get; init; }

    /// <summary>
    /// The unique identifier for the dataset within the knowledge graph.
    /// When null, the user's default dataset is used.
    /// </summary>
    public string? DatasetId { get; init; }

    /// <summary>
    /// The user ID of the dataset owner/creator. May differ from <see cref="UserId"/>
    /// when a user has been granted access to another user's dataset.
    /// </summary>
    public string? DatasetOwnerId { get; init; }
}
