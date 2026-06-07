using Domain.AI.Changes;

namespace Application.AI.Common.Interfaces.Changes;

/// <summary>
/// One inspection or action step in the <c>ChangeProposalOrchestrator</c>'s sequential
/// pipeline. Gates are stateless; all state lives on the <see cref="ChangeProposal"/>
/// they evaluate and on the <see cref="GateContext"/> the orchestrator hands them.
/// </summary>
/// <remarks>
/// <para>
/// Registration: each gate is added to DI as a keyed transient under its <see cref="Key"/>
/// (<c>AddKeyedTransient&lt;IChangeProposalGate&gt;("self_validation", ...)</c>). The
/// orchestrator iterates a proposal's <c>RequiredGates</c> in order and resolves each
/// gate from keyed DI by that string key. There is no "skip" — a gate is either
/// required (registered + listed in <c>RequiredGates</c>) or not run.
/// </para>
/// <para>
/// Implementations must be thread-safe (a single gate instance may evaluate many
/// proposals concurrently) and idempotent (the orchestrator may re-invoke the same
/// gate after a <c>Defer</c> retry, possibly with different <see cref="GateContext.AttemptCount"/>).
/// </para>
/// <para>
/// Implementations must not mutate the proposal — state machine transitions are the
/// orchestrator's responsibility. Throwing escapes the gate's contract entirely; the
/// orchestrator catches and rejects the proposal with the exception recorded in the
/// gate history.
/// </para>
/// </remarks>
public interface IChangeProposalGate
{
    /// <summary>
    /// The string key this gate registers under in keyed DI and the value that appears
    /// in a proposal's <c>RequiredGates</c> list and in <c>GateDecision.GateKey</c>.
    /// Convention: lowercase snake_case (<c>self_validation</c>, <c>policy</c>,
    /// <c>approval</c>, <c>merge</c>).
    /// </summary>
    string Key { get; }

    /// <summary>
    /// The orchestrator phase this gate runs in. The orchestrator partitions a
    /// proposal's <c>RequiredGates</c> by querying each gate's <see cref="Phase"/>;
    /// gate keys whose phase is <see cref="GatePhase.Validation"/> run in the
    /// validation loop, <see cref="GatePhase.Approval"/> drives the
    /// AwaitingApproval transition, and <see cref="GatePhase.Merge"/> runs in
    /// the merge loop. Declared on the gate so a custom gate added by a skill
    /// pack can never silently land in the wrong phase.
    /// </summary>
    GatePhase Phase { get; }

    /// <summary>
    /// Evaluate the proposal against this gate's responsibility.
    /// </summary>
    /// <param name="proposal">The proposal under evaluation. Must not be mutated.</param>
    /// <param name="context">Per-evaluation orchestrator context.</param>
    /// <param name="cancellationToken">Cancellation token honored by long-running validators / policy backends.</param>
    /// <returns>
    /// A <see cref="GateResult"/> indicating <see cref="GateAction.Pass"/>,
    /// <see cref="GateAction.Fail"/>, or <see cref="GateAction.Defer"/>. Defer is
    /// legitimate when an upstream system needs more time; Fail terminates the
    /// proposal at <c>Rejected</c>; Pass advances to the next gate.
    /// </returns>
    Task<GateResult> EvaluateAsync(
        ChangeProposal proposal,
        GateContext context,
        CancellationToken cancellationToken);
}
