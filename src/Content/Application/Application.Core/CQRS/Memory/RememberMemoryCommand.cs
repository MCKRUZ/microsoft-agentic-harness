using Domain.Common;
using MediatR;

namespace Application.Core.CQRS.Memory;

/// <summary>
/// Stores a fact in the caller's cross-session memory. The fact is evaluated by the memory write
/// gate (prompt-injection scan, trust classification, provenance stamping) before persistence, and
/// the gate's outcome is returned honestly — persisted, quarantined, or rejected — so the caller
/// knows whether the fact will ever be recalled.
/// </summary>
/// <remarks>
/// Identity scoping is server-side and automatic: the underlying <c>IKnowledgeMemory</c> namespaces
/// the node id as <c>memory:{tenant}:{user}:{key}</c> from the ambient knowledge scope, so a caller
/// can only ever write into their own namespace. No tenant or owner field exists on this command
/// by design.
/// </remarks>
public sealed record RememberMemoryCommand : IRequest<Result<RememberMemoryResult>>
{
    /// <summary>Caller-chosen key for the fact, unique within the caller's memory namespace.</summary>
    public required string Key { get; init; }

    /// <summary>The fact content to remember.</summary>
    public required string Content { get; init; }

    /// <summary>The default <see cref="EntityType"/> when a caller does not specify one.</summary>
    public const string DefaultEntityType = "Fact";

    /// <summary>The entity type stamped on the graph node (e.g. "Fact", "Preference"). Defaults to <see cref="DefaultEntityType"/>.</summary>
    public string EntityType { get; init; } = DefaultEntityType;
}
