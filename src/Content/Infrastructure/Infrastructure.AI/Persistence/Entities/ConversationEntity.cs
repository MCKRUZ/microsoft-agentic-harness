namespace Infrastructure.AI.Persistence.Entities;

/// <summary>
/// EF Core entity for the conversation header — everything about a transcript except its messages,
/// which live one row each in <see cref="ConversationMessageEntity"/>.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately carries no concurrency token. Every mutation this entity supports is a targeted
/// single-column write whose correct semantics are last-write-wins (the newest telemetry snapshot,
/// the newest settings, the newest <c>UpdatedAt</c>), and each is issued as a direct <c>UPDATE</c>
/// rather than a read-modify-write. Adding a version token would convert two harmless concurrent
/// telemetry writes into a thrown <c>DbUpdateConcurrencyException</c> — a regression against the
/// file-backed store this one replaces. Serialising a whole *turn* is a different problem, solved
/// by the turn lease rather than by a token on this row.
/// </para>
/// </remarks>
public sealed class ConversationEntity
{
    /// <summary>Conversation identifier. Caller-supplied or a generated GUID string.</summary>
    public required string Id { get; set; }

    /// <summary>Name of the agent this conversation is bound to.</summary>
    public required string AgentName { get; set; }

    /// <summary>Object ID (OID claim) of the owning user. Ownership is checked by the caller.</summary>
    public required string UserId { get; set; }

    /// <summary>When the conversation was created.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When the conversation was last written to.</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Title derived from the first user message. Null until that message arrives.</summary>
    public string? Title { get; set; }

    /// <summary>
    /// Serialized <c>ConversationSettings</c>, or null when the conversation has none.
    /// </summary>
    /// <remarks>
    /// Held as JSON rather than as three nullable columns because "no settings" and "settings whose
    /// every field is null" are different states the store must round-trip, and separate columns
    /// cannot tell them apart. Nothing queries inside this value.
    /// </remarks>
    public string? SettingsJson { get; set; }

    /// <summary>Observability session id carried across stateless HTTP requests. Null until set.</summary>
    public Guid? ObservabilitySessionId { get; set; }

    /// <summary>Serialized <c>TelemetryAccumulator</c>, or null before the first turn completes.</summary>
    public string? TelemetryJson { get; set; }

    /// <summary>
    /// Token identifying whoever currently holds this conversation's turn lease, or null when no
    /// turn is running. Written only by <c>SqliteConversationTurnLease</c>.
    /// </summary>
    /// <remarks>
    /// Opaque, and unique per acquisition rather than per host: renewal and release both match on it,
    /// so a host that lost its lease and legitimately re-took it must not be able to renew the
    /// abandoned one. Its shape (machine, process, GUID) exists to make a stuck lease readable in the
    /// database; nothing parses it.
    /// </remarks>
    public string? LeaseOwner { get; set; }

    /// <summary>
    /// When the current lease stops being valid, or null when no turn is running. A lease at or past
    /// this instant is claimable by anyone, which is what keeps a host that died mid-turn from
    /// blocking the conversation permanently.
    /// </summary>
    public DateTimeOffset? LeaseExpiresAt { get; set; }

    /// <summary>Messages belonging to this conversation, in append order by ordinal.</summary>
    public ICollection<ConversationMessageEntity> Messages { get; set; } = [];
}
