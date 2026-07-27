using Application.AI.Common.Interfaces.Changes;
using Domain.AI.Changes;
using Domain.Common;
using Domain.Common.Config;
using MediatR;
using Microsoft.Extensions.Options;

namespace Application.AI.Common.CQRS.Changes.ApproveChangeProposal;

/// <summary>
/// Handles <see cref="ApproveChangeProposalCommand"/>: load the proposal,
/// transition to <see cref="ChangeProposalStatus.Approved"/>, append the
/// gate-history entry, persist, and enqueue the proposal on the
/// <see cref="IChangeProposalDispatchQueue"/> so the background worker
/// advances it through Merging to Merged (or Rejected on apply failure).
/// Returns the Approved snapshot immediately; the caller polls the read
/// model for the final outcome.
/// </summary>
/// <remarks>
/// Behaviour change from inline-orchestrator: the command no longer blocks
/// on the merge phase. A 20-second merge call against GitHub used to keep
/// the HTTP response open the whole time; behind a tight proxy timeout the
/// caller dropped while the orchestrator finished. The dispatch queue
/// decouples the response from the merge wall-clock.
/// </remarks>
public sealed class ApproveChangeProposalCommandHandler
    : IRequestHandler<ApproveChangeProposalCommand, Result<ChangeProposal>>
{
    /// <summary>The keyed-DI key recorded on the approval gate decision in the audit history.</summary>
    public const string ApprovalGateKey = "approval";

    private readonly IChangeProposalStore _store;
    private readonly IChangeProposalDispatchQueue _dispatchQueue;
    private readonly IOptionsMonitor<AppConfig> _config;
    private readonly TimeProvider _time;

    /// <summary>Initializes a new <see cref="ApproveChangeProposalCommandHandler"/>.</summary>
    public ApproveChangeProposalCommandHandler(
        IChangeProposalStore store,
        IChangeProposalDispatchQueue dispatchQueue,
        IOptionsMonitor<AppConfig> config,
        TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(dispatchQueue);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(time);

        _store = store;
        _dispatchQueue = dispatchQueue;
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

        return await ChangeProposalCommandHelper.ApplyDecisionAsync(
            _store,
            request.ProposalId,
            statusGuard: p => p.Status != ChangeProposalStatus.AwaitingApproval
                ? Result<ChangeProposal>.Conflict(
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
                // Hand off to the background worker for the merge phase.
                // Approved is a transient status; the orchestrator will flip
                // it to Merging then Merged (or Rejected on apply failure)
                // out-of-band so this command doesn't block on the merge
                // wall-clock.
                await _dispatchQueue.EnqueueAsync(approved.Id, ct).ConfigureAwait(false);
                return Result<ChangeProposal>.Success(approved);
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}
