using Domain.AI.Changes;
using Domain.AI.Identity;

namespace Application.AI.Common.Interfaces.Changes;

/// <summary>
/// Append-only audit sink for <c>ChangeProposal</c> gate decisions. Each
/// gate evaluation produces exactly one entry, written before the proposal's
/// state machine advances so the audit is durable even if the orchestrator
/// crashes mid-pipeline.
/// </summary>
public interface IChangeAuditWriter
{
    /// <summary>
    /// Append a gate decision to the audit.
    /// </summary>
    /// <param name="proposal">The proposal under evaluation.</param>
    /// <param name="decision">The gate decision to record.</param>
    /// <param name="identity">The submitting agent identity (denormalized into the audit line).</param>
    /// <param name="mode">The orchestrator mode at the moment of the decision — distinguishes Shadow runs in the audit.</param>
    /// <param name="correlationId">Correlation id stitching this entry to other log/trace records for the same orchestrator run.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task AppendAsync(
        ChangeProposal proposal,
        GateDecision decision,
        AgentIdentity identity,
        OrchestratorMode mode,
        string correlationId,
        CancellationToken cancellationToken);
}
