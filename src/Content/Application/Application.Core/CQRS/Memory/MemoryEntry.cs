namespace Application.Core.CQRS.Memory;

/// <summary>
/// A slim, wire-safe projection of a recalled memory graph node. Deliberately excludes the node's
/// internals — <c>OwnerId</c>, <c>TenantId</c>, <c>ProvenanceStamp</c>, trust markers, and the raw
/// properties bag — so the HTTP surface never echoes identity scoping or gate metadata back to the
/// caller. Only quarantine-free (trusted) nodes ever reach this projection, because recall itself
/// filters untrusted facts.
/// </summary>
public sealed record MemoryEntry
{
    /// <summary>The memory key the fact was stored under (the graph node's name).</summary>
    public required string Key { get; init; }

    /// <summary>
    /// The remembered fact content. Empty when the recalled node is a corpus entity rather than a
    /// remembered fact (entity nodes carry no <c>content</c> property).
    /// </summary>
    public required string Content { get; init; }

    /// <summary>The entity type stamped at write time (e.g. "Fact", "Preference").</summary>
    public required string EntityType { get; init; }

    /// <summary>
    /// When the node was created in the knowledge graph, or <see langword="null"/> when the
    /// backing store did not stamp a creation time.
    /// </summary>
    public DateTimeOffset? CreatedAt { get; init; }
}
