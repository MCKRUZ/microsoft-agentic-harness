using Domain.AI.KnowledgeGraph.Models;

namespace Infrastructure.AI.KnowledgeGraph.Remote;

// Wire-shape DTOs for the remote memory-hosting HTTP contract. Kept internal and separate from
// this harness's own domain records (ConversationFact, MemoryWriteDecision, GraphNode) because the
// wire shape is an external contract this harness does not control, while the domain records are
// this harness's own — collapsing them into one type would make an unrelated change on the remote
// side (a renamed JSON field) a breaking change to this harness's public interfaces.

/// <summary>Request body for <c>POST {avatarId}/extract</c>.</summary>
internal sealed record ExtractFactsRequest
{
    public required string ThreadId { get; init; }
    public required string UserMessage { get; init; }
    public required string AssistantResponse { get; init; }
    public required int TurnNumber { get; init; }
    public required string RunId { get; init; }
}

/// <summary>One element of the array <c>POST {avatarId}/extract</c> returns.</summary>
internal sealed record ExtractedFactResponse
{
    public string? Key { get; init; }
    public string? Content { get; init; }
    public string EntityType { get; init; } = "Fact";
    public double Confidence { get; init; }
}

/// <summary>
/// Request body for <c>POST {avatarId}/remember</c>. <see cref="UserId"/>/<see cref="TenantId"/>
/// are sent so a remote service that partitions its store by caller can honor the same
/// per-user/per-tenant isolation this harness's local backend enforces; the current avatar
/// contract does not yet filter on them, so isolation still depends on deployment topology
/// (one harness+avatar pair per isolation boundary) until it does.
/// </summary>
internal sealed record RememberRequest
{
    public required string ThreadId { get; init; }
    public required string Key { get; init; }
    public required string Content { get; init; }
    public required string EntityType { get; init; }
    public required string UserId { get; init; }
    public string? TenantId { get; init; }
}

/// <summary>
/// Response body for <c>POST {avatarId}/remember</c>. <see cref="Trust"/> deserializes directly
/// from the remote service's plain (non-string) enum encoding — its ordinal values (Trusted=0,
/// Untrusted=1) happen to match <see cref="MemoryTrust"/>'s own, and neither side has a
/// <c>JsonStringEnumConverter</c> configured, so the default numeric enum deserialization is
/// exactly the intended mapping, not a coincidence to work around.
/// </summary>
internal sealed record RememberResponse
{
    public bool Persist { get; init; }
    public MemoryTrust Trust { get; init; }
    public string Reason { get; init; } = string.Empty;
}

/// <summary>
/// Request body for <c>POST {avatarId}/recall</c>. <see cref="UserId"/>/<see cref="TenantId"/> are
/// sent for the same forward-compatibility reason as <see cref="RememberRequest"/> — see its remarks.
/// </summary>
internal sealed record RecallRequest
{
    public required string Query { get; init; }
    public int? MaxResults { get; init; }
    public required string UserId { get; init; }
    public string? TenantId { get; init; }
}

/// <summary>One element of the array <c>POST {avatarId}/recall</c> returns.</summary>
internal sealed record RecalledMemoryResponse
{
    public string? Id { get; init; }
    public string? Content { get; init; }
    public double Score { get; init; }
}
