using Application.AI.Common.Interfaces.Changes;
using Domain.AI.Changes;
using Domain.Common;
using Domain.Common.Config;
using MediatR;
using Microsoft.Extensions.Options;

namespace Application.AI.Common.CQRS.Changes.ApproveChangeProposal;

/// <summary>
/// Handles <see cref="ApproveChangeProposalCommand"/>: load the proposal, transition
/// to <see cref="ChangeProposalStatus.Approved"/>, append the gate-history entry,
/// persist, and inline-drive the orchestrator so the proposal advances through
/// Merging to Merged (or Rejected on apply failure) before the command returns.
/// </summary>
public sealed class ApproveChangeProposalCommandHandler
    : IRequestHandler<ApproveChangeProposalCommand, Result<ChangeProposal>>
{
    /// <summary>The keyed-DI key recorded on the approval gate decision in the audit history.</summary>
    public const string ApprovalGateKey = "approval";

    private readonly IChangeProposalStore _store;
    private readonly IChangeProposalOrchestrator _orchestrator;
    private readonly IOptionsMonitor<AppConfig> _config;
    private readonly TimeProvider _time;

    /// <summary>Initializes a new <see cref="ApproveChangeProposalCommandHandler"/>.</summary>
    public ApproveChangeProposalCommandHandler(
        IChangeProposalStore store,
        IChangeProposalOrchestrator orchestrator,
        IOptionsMonitor<AppConfig> config,
        TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(orchestrator);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(time);

        _store = store;
        _orchestrator = orchestrator;
        _config = config;
        _time = time;
    }

    /// <inheritdoc />
    public async Task<Result<ChangeProposal>> Handle(
        ApproveChangeProposalCommand request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var changesConfig = _config.CurrentValue.AI.Changes;
        if (!changesConfig.Enabled)
        {
            return Result<ChangeProposal>.Forbidden(
                "ChangeProposal pipeline is disabled. Set AppConfig.AI.Changes.Enabled = true to enable.");
        }

        var mode = ParseMode(changesConfig.DefaultMode);

        return await ChangeProposalCommandHelper.ApplyDecisionAsync(
            _store,
            request.ProposalId,
            statusGuard: p => p.Status != ChangeProposalStatus.AwaitingApproval
                ? Result<ChangeProposal>.Fail(
                    $"Cannot approve proposal in status {p.Status} (must be AwaitingApproval).")
                : null,
            decisionFactory: () => new GateDecision
            {
                Timestamp = _time.GetUtcNow(),
                GateKey = ApprovalGateKey,
                Action = GateAction.Pass,
                Reason = string.IsNullOrEmpty(request.Reason) ? "approved" : request.Reason,
                ReviewerId = request.ReviewerId,
                DurationMs = 0
            },
            targetStatus: ChangeProposalStatus.Approved,
            postSave: async (approved, ct) =>
            {
                // Drive the orchestrator inline so Approved → Merging → Merged
                // (or Rejected on apply failure) happens before the command
                // returns. ProcessAsync returns null only when the store lost
                // the proposal between our Save and its Get — surface as
                // NotFound rather than returning the stale Approved snapshot,
                // which would lie about the merge phase having completed.
                var processed = await _orchestrator.ProcessAsync(approved.Id, mode, ct).ConfigureAwait(false);
                return processed is null
                    ? Result<ChangeProposal>.NotFound(
                        $"ChangeProposal '{approved.Id}' was deleted before the orchestrator could process it.")
                    : Result<ChangeProposal>.Success(processed);
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static OrchestratorMode ParseMode(string raw) =>
        Enum.TryParse<OrchestratorMode>(raw, ignoreCase: true, out var mode) ? mode : OrchestratorMode.Shadow;
}
