using Application.AI.Common.Interfaces.KnowledgeGraph;

namespace Application.AI.Common.Services.KnowledgeGraph;

/// <summary>
/// Null-object <see cref="IKnowledgeScope"/> representing an anonymous / system execution
/// context: every scope property is <see langword="null"/>.
/// </summary>
/// <remarks>
/// <para>
/// Registered as a <c>TryAddScoped</c> fallback by subsystems that consume the ambient
/// knowledge scope (e.g. the planner state store) so they stay resolvable — and
/// <c>ValidateOnBuild</c>-safe — in hosts that do not wire the knowledge graph layer.
/// Composed hosts register the real <c>KnowledgeScopeAccessor</c> (which flows caller
/// identity via <see cref="AsyncLocal{T}"/>) and that registration wins resolution.
/// </para>
/// <para>
/// Under this scope, ownership stamping writes <see langword="null"/> owner/tenant
/// (a global record) and scope filtering exposes only global records — the closed-by-default
/// posture for callers without identity.
/// </para>
/// </remarks>
public sealed class NullKnowledgeScope : IKnowledgeScope
{
    /// <inheritdoc />
    public string? UserId => null;

    /// <inheritdoc />
    public string? TenantId => null;

    /// <inheritdoc />
    public string? DatasetId => null;

    /// <inheritdoc />
    public string? DatasetName => null;

    /// <inheritdoc />
    public string? DatasetOwnerId => null;

    /// <inheritdoc />
    public string? AgentId => null;

    /// <inheritdoc />
    public string? ConversationId => null;
}
