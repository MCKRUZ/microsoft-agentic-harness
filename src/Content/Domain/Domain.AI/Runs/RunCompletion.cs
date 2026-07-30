namespace Domain.AI.Runs;

/// <summary>
/// How a run ended, as reported by the executor that performed it.
/// </summary>
/// <remarks>
/// <para>
/// Work can end in more ways than "it worked" and "it did not". A workflow that parks on a human gate
/// produced no result and suffered no fault; an operator cancel is a deliberate stop. Collapsing those
/// into a boolean forces the dispatcher to guess, and the guess that gets made is
/// <see cref="RunStatus.Succeeded"/> — telling a caller polling for an outcome that work finished when
/// it is actually sitting awaiting an approval nobody has been asked for.
/// </para>
/// <para>
/// This is the executor's answer to "what happened", separate from whether the executor was
/// <em>able</em> to answer. An executor that could not run the work at all reports a failed
/// <c>Result</c> instead, and the dispatcher records that as <see cref="RunStatus.Failed"/>.
/// </para>
/// </remarks>
public sealed record RunCompletion
{
    /// <summary>The terminal state the run reached.</summary>
    public required RunStatus Status { get; init; }

    /// <summary>
    /// Caller-safe explanation of the outcome, when one adds anything. Never raw exception text.
    /// </summary>
    public string? Detail { get; init; }

    /// <summary>
    /// The decisions a parked run is waiting on. Empty for every other status.
    /// </summary>
    /// <remarks>
    /// This is what makes a park recoverable. A run that parks without naming what it waits on can
    /// only ever be released by the host's parked-run ceiling — which fails it — so the ids are
    /// carried from the executor that knows them rather than rediscovered later by a reader that
    /// would have to reach into another subsystem's state to find them.
    /// </remarks>
    public IReadOnlyList<Guid> AwaitingEscalationIds { get; init; } = [];

    /// <summary>The run produced its result.</summary>
    public static RunCompletion Succeeded() => new() { Status = RunStatus.Succeeded };

    /// <summary>The run ran but did not produce its result.</summary>
    /// <param name="detail">Caller-safe reason.</param>
    public static RunCompletion Failed(string detail) =>
        new() { Status = RunStatus.Failed, Detail = detail };

    /// <summary>The run was stopped on request before it could finish.</summary>
    /// <param name="detail">Caller-safe reason.</param>
    public static RunCompletion Cancelled(string detail) =>
        new() { Status = RunStatus.Cancelled, Detail = detail };

    /// <summary>The run parked awaiting a decision it cannot make for itself.</summary>
    /// <param name="detail">Caller-safe explanation of what is being waited on.</param>
    /// <param name="awaitingEscalationIds">
    /// The decisions whose verdicts release the run. Required rather than optional: an empty list is a
    /// legitimate answer — a plan can park with no readable escalation reference — but it means the run
    /// is unrecoverable except by the host's parked-run ceiling, and a caller should have to say so
    /// rather than arrive there by omitting an argument.
    /// </param>
    public static RunCompletion Blocked(string detail, IReadOnlyList<Guid> awaitingEscalationIds) =>
        new()
        {
            Status = RunStatus.Blocked,
            Detail = detail,
            AwaitingEscalationIds = awaitingEscalationIds
        };
}
