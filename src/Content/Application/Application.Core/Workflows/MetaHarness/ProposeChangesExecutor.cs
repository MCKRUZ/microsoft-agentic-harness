using Application.AI.Common.Interfaces.MetaHarness;
using Domain.Common.MetaHarness;
using Microsoft.Agents.AI.Workflows;

namespace Application.Core.Workflows.MetaHarness;

/// <summary>
/// Invokes <see cref="IHarnessProposer"/> to generate a new harness configuration proposal,
/// creates a <see cref="HarnessCandidate"/> from the proposal, and persists it.
/// First step in the optimization iteration workflow.
/// </summary>
public sealed class ProposeChangesExecutor(
    IHarnessProposer proposer,
    IHarnessCandidateRepository candidateRepository)
    : Executor<OptimizationIterationInput, ProposalStepOutput>("ProposeChanges")
{
    /// <summary>
    /// Runs the proposer agent to generate a harness change proposal, creates a candidate
    /// record from the proposal with <see cref="HarnessCandidateStatus.Proposed"/> status,
    /// and persists it to the candidate repository.
    /// </summary>
    /// <param name="message">The optimization iteration input with proposer context.</param>
    /// <param name="context">The MAF workflow context.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="ProposalStepOutput"/> containing the proposal and candidate.</returns>
    public override async ValueTask<ProposalStepOutput> HandleAsync(
        OptimizationIterationInput message,
        IWorkflowContext context,
        CancellationToken cancellationToken = default)
    {
        var proposal = await proposer.ProposeAsync(message.ProposerContext, cancellationToken);

        var currentSnapshot = message.ProposerContext.CurrentCandidate.Snapshot;
        var newSnapshot = ApplyProposalToSnapshot(currentSnapshot, proposal);

        var candidate = new HarnessCandidate
        {
            CandidateId = Guid.NewGuid(),
            OptimizationRunId = message.ProposerContext.CurrentCandidate.OptimizationRunId,
            ParentCandidateId = message.ProposerContext.CurrentCandidate.CandidateId,
            Iteration = message.Iteration,
            CreatedAt = DateTimeOffset.UtcNow,
            Snapshot = newSnapshot,
            Status = HarnessCandidateStatus.Proposed
        };

        await candidateRepository.SaveAsync(candidate, cancellationToken);

        return new ProposalStepOutput(proposal, candidate);
    }

    private static HarnessSnapshot ApplyProposalToSnapshot(
        HarnessSnapshot current,
        HarnessProposal proposal)
    {
        var mergedSkills = new Dictionary<string, string>(current.SkillFileSnapshots);
        foreach (var (path, content) in proposal.ProposedSkillChanges)
        {
            mergedSkills[path] = content;
        }

        var mergedConfig = new Dictionary<string, string>(current.ConfigSnapshot);
        foreach (var (key, value) in proposal.ProposedConfigChanges)
        {
            mergedConfig[key] = value;
        }

        var systemPrompt = proposal.ProposedSystemPromptChange ?? current.SystemPromptSnapshot;

        return new HarnessSnapshot
        {
            SkillFileSnapshots = mergedSkills,
            SystemPromptSnapshot = systemPrompt,
            ConfigSnapshot = mergedConfig,
            SnapshotManifest = current.SnapshotManifest
        };
    }
}
