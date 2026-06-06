using Domain.AI.Changes;
using Domain.Common;
using MediatR;

namespace Application.AI.Common.CQRS.Changes.SubmitChangeProposal;

/// <summary>
/// Submit a new <see cref="ChangeProposal"/> for evaluation by the gate pipeline.
/// The agent-side entrypoint for the entire PR-2 surface.
/// </summary>
/// <remarks>
/// <para>
/// The handler resolves <see cref="RequiredGates"/> from
/// <c>IChangeProposalGateResolver</c> when not explicitly supplied, derives the
/// deterministic id via <c>ChangeProposalIdDeriver</c>, persists the proposal in
/// <c>Draft</c> status, and returns it. Re-submitting a logically identical
/// proposal within the same id-bucket returns the prior proposal verbatim — the
/// orchestrator never starts a duplicate pipeline.
/// </para>
/// <para>
/// <see cref="SubmittedAt"/> defaults to the handler's <c>TimeProvider</c>. Pass an
/// explicit value only for testing or replaying historical submissions.
/// </para>
/// </remarks>
public sealed record SubmitChangeProposalCommand : IRequest<Result<ChangeProposal>>
{
    /// <summary>The target the change will apply to.</summary>
    public required ChangeTarget Target { get; init; }

    /// <summary>The ordered list of bounded edits the proposal applies.</summary>
    public required IReadOnlyList<ChangeEdit> Diff { get; init; }

    /// <summary>Short human-readable summary; surfaces in approval prompts and audit.</summary>
    public required string Summary { get; init; }

    /// <summary>Submitter's impact-radius estimate; advisory at submission, confirmed by gates.</summary>
    public required BlastRadius BlastRadius { get; init; }

    /// <summary>
    /// Optional explicit override of the gate pipeline. When null the handler
    /// resolves the default from <c>IChangeProposalGateResolver</c>. Use the
    /// override sparingly — bypassing the resolver bypasses its
    /// "Critical always needs approval" enforcement.
    /// </summary>
    public IReadOnlyList<string>? RequiredGates { get; init; }

    /// <summary>
    /// Optional override of the wall-clock submission instant. When null the handler
    /// reads <c>TimeProvider.GetUtcNow</c>. Used for tests and replay tooling.
    /// </summary>
    public DateTimeOffset? SubmittedAt { get; init; }
}
