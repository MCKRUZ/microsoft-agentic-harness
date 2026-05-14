using Application.AI.Common.Interfaces.MetaHarness;
using Domain.Common.MetaHarness;

namespace Application.Core.Workflows.MetaHarness;

/// <summary>
/// Input to a single optimization iteration within a meta-harness optimization run.
/// Contains the proposer context, current best candidate, and iteration index.
/// </summary>
/// <param name="ProposerContext">
/// The context passed to <see cref="IHarnessProposer.ProposeAsync"/> containing
/// the current candidate, run directory, prior candidates, and accumulated learnings.
/// </param>
/// <param name="CurrentBest">
/// The best-scoring candidate so far, or <c>null</c> on the first iteration (seed).
/// Used by <see cref="ScoreCandidateExecutor"/> to determine if the new candidate is an improvement.
/// </param>
/// <param name="Iteration">Zero-based iteration index within the optimization run.</param>
public sealed record OptimizationIterationInput(
    HarnessProposerContext ProposerContext,
    HarnessCandidate? CurrentBest,
    int Iteration);

/// <summary>
/// Output of the proposal step. Contains both the raw proposal and the persisted
/// candidate record in <see cref="HarnessCandidateStatus.Proposed"/> status.
/// </summary>
/// <param name="Proposal">The harness changes proposed by <see cref="IHarnessProposer"/>.</param>
/// <param name="Candidate">
/// The <see cref="HarnessCandidate"/> record created from the proposal, persisted
/// with <see cref="HarnessCandidateStatus.Proposed"/> status.
/// </param>
public sealed record ProposalStepOutput(
    HarnessProposal Proposal,
    HarnessCandidate Candidate);

/// <summary>
/// Output of the evaluation step. Contains the aggregated evaluation result and
/// the candidate updated with its score.
/// </summary>
/// <param name="EvaluationResult">
/// Aggregated pass rate, token cost, and per-task results from
/// <see cref="IEvaluationService.EvaluateAsync"/>.
/// </param>
/// <param name="Candidate">
/// The candidate record updated with <see cref="HarnessCandidateStatus.Evaluated"/>
/// status, <see cref="HarnessCandidate.BestScore"/>, and <see cref="HarnessCandidate.TokenCost"/>.
/// </param>
public sealed record EvaluationStepOutput(
    EvaluationResult EvaluationResult,
    HarnessCandidate Candidate);

/// <summary>
/// Final outcome of one optimization iteration. Indicates whether the candidate
/// improved upon the current best and includes the reasoning from the proposal.
/// </summary>
/// <param name="Candidate">The fully evaluated and persisted candidate.</param>
/// <param name="Score">The candidate's pass rate in the range [0.0, 1.0].</param>
/// <param name="IsImprovement">
/// <c>true</c> if the candidate's score exceeds the current best (or is the first candidate).
/// </param>
/// <param name="Reasoning">The proposer agent's explanation of why the changes were made.</param>
public sealed record IterationOutcome(
    HarnessCandidate Candidate,
    double Score,
    bool IsImprovement,
    string Reasoning);
