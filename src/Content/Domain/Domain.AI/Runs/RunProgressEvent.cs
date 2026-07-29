namespace Domain.AI.Runs;

/// <summary>
/// Something that happened part-way through a run, as reported to whoever is watching it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Kind-agnostic, like the rest of the substrate.</strong> Every run has a shape a watcher
/// cares about — it started, a unit of work began, a unit of work ended, it finished — and nothing
/// here is specific to workflows. A new <see cref="RunKind"/> becomes an executor that publishes these,
/// not a second streaming endpoint with its own event vocabulary.
/// </para>
/// <para>
/// <strong><see cref="Sequence"/> is what makes a gap detectable.</strong> A watcher that cannot keep
/// up has events dropped rather than being allowed to slow the run down, and a dropped event must be
/// visible: a stream that silently skips one is worse than a stream that admits it, because the
/// watcher believes it saw everything.
/// </para>
/// <para>
/// Every field here is already visible to the caller through the run's own status. Nothing carries
/// step output, prompts, or tool arguments — a progress feed says how far along the work is, not what
/// the work said.
/// </para>
/// </remarks>
public sealed record RunProgressEvent
{
    /// <summary>The run this describes.</summary>
    public required string JobId { get; init; }

    /// <summary>Position in this run's event order, starting at 1 and never reused.</summary>
    public required long Sequence { get; init; }

    /// <summary>What happened.</summary>
    public required RunProgressKind Kind { get; init; }

    /// <summary>When it happened.</summary>
    public required DateTimeOffset OccurredAt { get; init; }

    /// <summary>Identifier of the step involved, for the step-scoped kinds.</summary>
    public string? StepId { get; init; }

    /// <summary>Human-readable name of the step involved, for the step-scoped kinds.</summary>
    public string? StepName { get; init; }

    /// <summary>
    /// Where the thing this describes has got to — a step's execution status, or the run's own
    /// terminal status on <see cref="RunProgressKind.RunFinished"/>.
    /// </summary>
    public string? Status { get; init; }

    /// <summary>Caller-safe elaboration, when there is one. Never raw exception or model text.</summary>
    public string? Detail { get; init; }
}

/// <summary>
/// The kinds of thing a run reports while it is running.
/// </summary>
/// <remarks>
/// Deliberately few. This is the vocabulary every kind of run can honestly speak, so it stays the
/// intersection rather than the union — a richer, kind-specific feed belongs to that kind, not here.
/// </remarks>
public enum RunProgressKind
{
    /// <summary>The run began executing. Emitted once, before any step event.</summary>
    RunStarted = 0,

    /// <summary>A step began executing.</summary>
    StepStarted = 1,

    /// <summary>A step finished, successfully or otherwise.</summary>
    StepCompleted = 2,

    /// <summary>The run reached a terminal state. Emitted once, last.</summary>
    RunFinished = 3,

    /// <summary>
    /// The run parked awaiting a decision it cannot make for itself, such as a human approval gate.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="RunFinished"/> because the run is not over — answering the gate
    /// continues it under the same job id. It ends a <em>stream</em> without ending the <em>run</em>:
    /// nothing further will be published until somebody acts, and an approver may take days, so a
    /// watcher is told to stop waiting rather than being left holding a connection. A client that
    /// wants to follow the rest of the run opens a new stream once it has answered.
    /// </remarks>
    RunParked = 4
}
