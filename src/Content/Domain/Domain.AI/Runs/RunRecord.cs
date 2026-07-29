using Domain.AI.Bundles;

namespace Domain.AI.Runs;

/// <summary>
/// One queued or completed unit of externally-triggered work: what it runs, who owns it, where it has
/// got to, and how it ended.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Deliberately kind-agnostic.</strong> Everything here is true of any run — identity,
/// ownership, lifecycle, timing — so a new kind of work becomes a <see cref="RunKind"/> member and an
/// executor registration rather than a second copy of the queue, dispatcher, single-arming and
/// expiry logic. What each kind needs beyond this belongs to that kind's own stored record, not here.
/// </para>
/// <para>
/// <strong><see cref="OwnerId"/> and <see cref="TenantId"/> are captured at creation, not read back
/// from an ambient scope later.</strong> A run outlives the request that started it and executes on a
/// background thread with no caller attached, so the identity that authorized it has to be carried on
/// the record itself. Reading it back ambiently would resolve to whatever scope the dispatcher
/// happened to be on — which is none.
/// </para>
/// </remarks>
public sealed record RunRecord
{
    /// <summary>Server-minted identifier the caller polls.</summary>
    public required string JobId { get; init; }

    /// <summary>Which kind of work this is, selecting the executor that will perform it.</summary>
    public required RunKind Kind { get; init; }

    /// <summary>
    /// Identifier of the thing being run, interpreted by the executor for <see cref="Kind"/> — a
    /// stored workflow's id for <see cref="RunKind.Workflow"/>.
    /// </summary>
    public required string TargetId { get; init; }

    /// <summary>Stable identity of the caller that started the run, and the only one that may read it.</summary>
    public required string OwnerId { get; init; }

    /// <summary>Tenant of the caller that started the run, when the host resolves one.</summary>
    public string? TenantId { get; init; }

    /// <summary>
    /// The grant this run executes under, resolved from the credential that <em>started</em> it.
    /// </summary>
    /// <remarks>
    /// Carried per run rather than re-resolved at execution time, so a run performs exactly what the
    /// caller who triggered it was entitled to at that moment — a later change to that caller's grant
    /// does not retroactively widen work already queued.
    /// </remarks>
    public required CapabilityEnvelope Envelope { get; init; }

    /// <summary>Where the run has got to.</summary>
    public required RunStatus Status { get; init; }

    /// <summary>Caller-safe failure reason once the run has failed. Never raw exception text.</summary>
    public string? Error { get; init; }

    /// <summary>When the run was accepted and queued.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>When a dispatcher claimed the run, if it has been claimed.</summary>
    public DateTimeOffset? StartedAt { get; init; }

    /// <summary>When the run reached a terminal state, if it has.</summary>
    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>Whether the run has finished and will not change again.</summary>
    public bool IsTerminal =>
        Status is RunStatus.Succeeded or RunStatus.Failed or RunStatus.Cancelled;
}
