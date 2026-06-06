using Application.AI.Common.Interfaces.Changes;
using Domain.AI.Changes;
using Domain.Common;
using MediatR;

namespace Application.AI.Common.CQRS.Changes.RunGate;

/// <summary>
/// Dispatch one gate evaluation against a proposal. Internal to the
/// <c>ChangeProposalOrchestrator</c> — external callers should submit, approve,
/// reject, or cancel, never run a gate directly.
/// </summary>
/// <remarks>
/// <para>
/// The handler loads the proposal by id, resolves the gate from keyed DI by
/// <see cref="GateKey"/>, builds a <see cref="GateContext"/>, runs the gate, and
/// returns its <see cref="GateResult"/>. Any state-machine transition resulting
/// from the gate's decision is the orchestrator's responsibility — this command
/// is the read-only "what does the gate say?" surface.
/// </para>
/// </remarks>
public sealed record RunGateCommand : IRequest<Result<GateResult>>
{
    /// <summary>The id of the proposal under evaluation.</summary>
    public required string ProposalId { get; init; }

    /// <summary>The keyed-DI key of the gate to run.</summary>
    public required string GateKey { get; init; }

    /// <summary>The orchestrator mode at the moment of this evaluation.</summary>
    public required OrchestratorMode Mode { get; init; }

    /// <summary>The 1-based attempt counter for this gate against this proposal.</summary>
    public required int AttemptCount { get; init; }

    /// <summary>Correlation id for stitching log lines + spans across the orchestrator run.</summary>
    public required string CorrelationId { get; init; }
}
