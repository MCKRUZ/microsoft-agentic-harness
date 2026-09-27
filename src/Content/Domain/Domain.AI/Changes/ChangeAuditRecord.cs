namespace Domain.AI.Changes;

/// <summary>
/// A single change-proposal gate decision, serialized as one hash-chained JSONL line in the
/// change audit trail (<c>changes.jsonl</c>). Promoted from a private nested type inside
/// <c>JsonlChangeAuditWriter</c> so it can be returned by a query API (#714) — the same shape
/// that has always been written to disk, unchanged, so existing audit files keep deserializing.
/// </summary>
/// <remarks>
/// <see cref="Mode"/> is stored as a plain string (the orchestrator mode's name, e.g.
/// <c>"Shadow"</c>/<c>"Live"</c>) rather than as its source enum type: that enum
/// (<c>OrchestratorMode</c>) lives in the Application layer, and a Domain record cannot
/// reference it without violating the dependency direction (Domain depends on nothing). This is
/// JSON-shape-compatible with the original serialization, which already wrote the enum as its
/// string name via <c>JsonStringEnumConverter</c> — the same trade-off <see cref="AgentIdentity"/>
/// makes for <see cref="ChangeAuditIdentity.Kind"/>.
/// </remarks>
public sealed record ChangeAuditRecord
{
    /// <summary>When this gate decision was recorded.</summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>The id of the proposal this decision was made on.</summary>
    public required string ProposalId { get; init; }

    /// <summary>The gate that produced this decision (e.g. <c>"MergeGate"</c>).</summary>
    public required string GateKey { get; init; }

    /// <summary>The gate's verdict.</summary>
    public required GateAction Decision { get; init; }

    /// <summary>Free-text explanation of the decision.</summary>
    public string Reason { get; init; } = string.Empty;

    /// <summary>Hash of the evidence backing this decision, if any.</summary>
    public string? EvidenceHash { get; init; }

    /// <summary>The reviewer who made this decision, for a human gate.</summary>
    public string? ReviewerId { get; init; }

    /// <summary>The proposal's blast radius at the time of this decision.</summary>
    public required BlastRadius BlastRadius { get; init; }

    /// <summary>The kind of target the proposal changes.</summary>
    public required ChangeTargetKind TargetKind { get; init; }

    /// <summary>
    /// The orchestrator mode (e.g. <c>"Shadow"</c>/<c>"Live"</c>) at the moment of this decision,
    /// as the enum's string name — see this type's remarks for why it is not the enum itself.
    /// </summary>
    public required string Mode { get; init; }

    /// <summary>Correlation id stitching this entry to other log/trace records for the same orchestrator run.</summary>
    public required string CorrelationId { get; init; }

    /// <summary>The identity of the agent that submitted or is running the proposal.</summary>
    public required ChangeAuditIdentity AgentIdentity { get; init; }

    /// <summary>How long the gate evaluation took, in milliseconds.</summary>
    public required long DurationMs { get; init; }
}
