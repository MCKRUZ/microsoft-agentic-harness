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
    /// <remarks>
    /// <para>
    /// <strong>This is also what admission's "one live run per target" rule is keyed on</strong>, which
    /// exists so two runs cannot share one workflow's plan state machine.
    /// </para>
    /// <para>
    /// <strong>A kind whose work has no shared target sets this to the run's own
    /// <see cref="JobId"/>.</strong> <see cref="RunKind.Evaluation"/> does: two callers evaluating one
    /// dataset are independent reads with nothing between them to corrupt. A unique target makes the
    /// exclusivity rule correctly inert for that kind, rather than switching a rule off where it still
    /// governs workflows. Do <em>not</em> reach for a constant instead — that would serialize an entire
    /// kind down to one concurrent run host-wide, which looks like a mysterious queue rather than a
    /// refusal.
    /// </para>
    /// </remarks>
    public required string TargetId { get; init; }

    /// <summary>Stable identity of the caller that started the run, and the only one that may read it.</summary>
    public required string OwnerId { get; init; }

    /// <summary>Tenant of the caller that started the run, when the host resolves one.</summary>
    public string? TenantId { get; init; }

    /// <summary>
    /// The grant this run executes under, resolved from the credential that <em>started</em> it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Carried per run rather than re-resolved at execution time, so a run performs exactly what the
    /// caller who triggered it was entitled to at that moment — a later change to that caller's grant
    /// does not retroactively widen work already queued.
    /// </para>
    /// <para>
    /// <strong>Accepted property, disclosed rather than discovered later: the grant is not
    /// re-evaluated when a parked run resumes.</strong> A workflow waiting on a human gate may sit for
    /// up to the host's <c>MaxParkedRunDuration</c> — a week by default — and it then continues under
    /// the grant its submitter held when it started, even if that grant has since been narrowed. It is
    /// a ceiling on what one caller could already do rather than a way to exceed it, and the window is
    /// operator-bounded; re-resolving it would need a credential to resolve <em>from</em>, and by then
    /// there is no request and no principal. A host that needs revocation to take effect sooner than a
    /// gate can be answered should shorten that ceiling.
    /// </para>
    /// </remarks>
    public required CapabilityEnvelope Envelope { get; init; }

    /// <summary>Where the run has got to.</summary>
    public required RunStatus Status { get; init; }

    /// <summary>Caller-safe failure reason once the run has failed. Never raw exception text.</summary>
    public string? Error { get; init; }

    /// <summary>When the run was accepted and queued.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// When the run first began executing, if it has. Not re-stamped when a parked run resumes — the
    /// work started when it started, and <see cref="ParkedAt"/> is what tracks the current wait.
    /// </summary>
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

    /// <summary>
    /// The decisions this run is parked on. Empty unless the run is parked.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Recorded on the run rather than rediscovered by whatever wants to resume it. The alternative is
    /// to ask the workflow's plan state which of its steps are blocked and on what — which makes the
    /// run substrate a reader of the planner's internals, and makes "which run is waiting on this
    /// decision" a scan of every plan rather than a lookup.
    /// </para>
    /// <para>
    /// Several at once is normal: a plan can reach two gates on parallel branches, park on both, and be
    /// resumable by either verdict — reconciliation on the next execution re-reads all of them.
    /// </para>
    /// </remarks>
    public IReadOnlyList<Guid> AwaitingEscalationIds { get; init; } = [];

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
