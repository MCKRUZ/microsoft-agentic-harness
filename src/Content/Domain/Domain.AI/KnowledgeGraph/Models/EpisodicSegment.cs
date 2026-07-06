namespace Domain.AI.KnowledgeGraph.Models;

/// <summary>
/// A raw, untruncated segment of what was <em>said</em> on a single conversation turn — the episodic
/// grounding record of the harmonic memory representation (Memora port). Distinct from a
/// <see cref="Domain.AI.WorkMemory.WorkEpisode"/> (what the agent <em>did</em>, deliberately truncated to
/// bound storage): an episodic segment preserves the conversation text verbatim, because truncation would
/// destroy the grounding value that later retrieval leans on.
/// </summary>
/// <remarks>
/// <para>
/// Segments are captured cheaply and structurally at the turn boundary — there is deliberately
/// <strong>no LLM call</strong> on the capture path, matching both the work-memory and harmonic-memory
/// designs. Only <em>factual</em> harmonic entries pay the abstraction/cue-anchor LLM cost; episodic
/// segments are stored raw.
/// </para>
/// <para>
/// Each segment cross-links to the <see cref="WorkEpisode"/> captured on the same turn via
/// <see cref="EpisodeId"/>, and both records share the <see cref="ConversationId"/> +
/// <see cref="TurnNumber"/> pair as a provenance key. The two schemas stay distinct (merging is lossy both
/// ways) but are mutually reachable: work-memory synthesis can walk to the raw grounding, and harmonic
/// recall can walk to the turn's outcome signal.
/// </para>
/// <para>
/// Tenant/owner isolation is enforced by the underlying graph store (the compliance-aware / tenant-isolating
/// decorator chain stamps tenant on write and filters on read); the segment itself carries no tenant field,
/// mirroring <see cref="WorkEpisode"/>.
/// </para>
/// </remarks>
public sealed record EpisodicSegment
{
    /// <summary>Unique identifier for this episodic segment.</summary>
    public required Guid SegmentId { get; init; }

    /// <summary>
    /// The <see cref="WorkEpisode.EpisodeId"/> of the work episode captured on the same turn — a best-effort
    /// convenience link that lets harmonic recall reach the turn's outcome signal without merging the two
    /// schemas. <see langword="null"/> when work memory is disabled (no episode was persisted for this turn):
    /// the authoritative, always-present correlation is the <see cref="ConversationId"/> +
    /// <see cref="TurnNumber"/> pair, so consumers must not treat this as a guaranteed-resolvable reference.
    /// </summary>
    public Guid? EpisodeId { get; init; }

    /// <summary>The agent that produced this turn — provenance for whose behaviour the segment grounds.</summary>
    public required string AgentId { get; init; }

    /// <summary>The conversation this turn belonged to. Provenance link + grouping key.</summary>
    public required string ConversationId { get; init; }

    /// <summary>The 1-based turn number within the conversation.</summary>
    public required int TurnNumber { get; init; }

    /// <summary>
    /// The raw, untruncated conversation content for the turn — what was said. Preserved verbatim; never
    /// summarized or capped, because the grounding value lives in the specifics.
    /// </summary>
    public required string Content { get; init; }

    /// <summary>When the segment was recorded (turn completion time).</summary>
    public required DateTimeOffset CreatedAt { get; init; }
}
