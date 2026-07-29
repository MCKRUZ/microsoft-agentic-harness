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

    /// <summary>When the run parked awaiting a decision, if it is parked.</summary>
    /// <remarks>
    /// Distinct from <see cref="StartedAt"/> because a run can park and resume more than once, and the
    /// thing that needs bounding is how long it has been waiting <em>this</em> time. A gate nobody
    /// answers is otherwise indistinguishable from one answered a moment ago.
    /// </remarks>
    public DateTimeOffset? ParkedAt { get; init; }

    /// <summary>Whether the run has finished and will not change again.</summary>
    /// <remarks>
    /// <para>
    /// Expressed as "not one of the live states" rather than "one of the terminal states" on purpose.
    /// The live states are the ones a run can still move out of, and enumerating the terminal side
    /// instead means every future outcome added to <see cref="RunStatus"/> is silently treated as live
    /// until someone remembers to list it here — which would strand it at the concurrency cap and keep
    /// it from ever being reclaimed.
    /// </para>
    /// <para>
    /// <see cref="RunStatus.Blocked"/> is live: a parked run resumes under this same job id when its
    /// gate is answered. Listing it here is what keeps its workflow locked — admission permits one live
    /// run per target, so a parked run read as finished would release its workflow to a second run
    /// against the same plan state machine.
    /// </para>
    /// </remarks>
    public bool IsTerminal =>
        Status is not (RunStatus.Queued or RunStatus.Running or RunStatus.Blocked);

    /// <summary>Whether the run is parked awaiting a decision it cannot make for itself.</summary>
    public bool IsAwaitingDecision => Status is RunStatus.Blocked;

    /// <summary>
    /// Whether the run will produce nothing further unless something outside it acts.
    /// </summary>
    /// <remarks>
    /// The question a progress watcher actually needs answered, and it is not
    /// <see cref="IsTerminal"/>. A parked run is live, but nothing will be published for it until a
    /// human answers its gate — so a stream that waited on it would hold a connection and a slot for
    /// as long as the approver took, having already said everything it can say. Both quiesced states
    /// mean "stop waiting"; only one of them means "this is over".
    /// </remarks>
    public bool IsQuiescent => IsTerminal || IsAwaitingDecision;
}
