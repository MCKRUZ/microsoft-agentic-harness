using Application.AI.Common.Interfaces.Changes;
using Domain.AI.Changes;
using Domain.Common;
using Domain.Common.Config;
using MediatR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Application.AI.Common.CQRS.Changes.ApproveChangeProposal;

/// <summary>
/// Handles <see cref="ApproveChangeProposalCommand"/>: load the proposal,
/// transition to <see cref="ChangeProposalStatus.Approved"/>, append the
/// decision to the durable audit chain and the gate history, persist, and
/// enqueue the proposal on the <see cref="IChangeProposalDispatchQueue"/> so the
/// background worker advances it through Merging to Merged (or Rejected on apply
/// failure). Returns the Approved snapshot immediately; the caller polls the read
/// model for the final outcome.
/// </summary>
/// <remarks>
/// <para>
/// Behaviour change from inline-orchestrator: the command no longer blocks
/// on the merge phase. A 20-second merge call against GitHub used to keep
/// the HTTP response open the whole time; behind a tight proxy timeout the
/// caller dropped while the orchestrator finished. The dispatch queue
/// decouples the response from the merge wall-clock.
/// </para>
/// <para>
/// The kill switch, the durable audit append, and the audit-before-save ordering
/// all live in <see cref="ChangeProposalCommandHelper.ApplyDecisionAsync"/> so
/// Approve, Reject, and Cancel cannot drift apart on them.
/// </para>
/// </remarks>
public sealed class ApproveChangeProposalCommandHandler
    : IRequestHandler<ApproveChangeProposalCommand, Result<ChangeProposal>>
{
    /// <summary>The keyed-DI key recorded on the approval gate decision in the audit history.</summary>
    public const string ApprovalGateKey = "approval";

    private readonly IChangeProposalStore _store;
    private readonly IChangeProposalDispatchQueue _dispatchQueue;
    private readonly IChangeAuditWriter _audit;
    private readonly IOptionsMonitor<AppConfig> _config;
    private readonly ILogger<ApproveChangeProposalCommandHandler> _logger;
    private readonly TimeProvider _time;

    /// <summary>Initializes a new <see cref="ApproveChangeProposalCommandHandler"/>.</summary>
    /// <param name="store">The proposal store.</param>
    /// <param name="dispatchQueue">Queue handing the approved proposal to the background merge worker.</param>
    /// <param name="audit">Durable hash-chained audit sink; the approval is appended before the state change is persisted.</param>
    /// <param name="config">Application configuration supplying the pipeline kill switch and orchestrator mode.</param>
    /// <param name="logger">Logger for kill-switch and audit-failure diagnostics.</param>
    /// <param name="time">Clock used to timestamp the gate decision.</param>
    public ApproveChangeProposalCommandHandler(
        IChangeProposalStore store,
        IChangeProposalDispatchQueue dispatchQueue,
        IChangeAuditWriter audit,
        IOptionsMonitor<AppConfig> config,
        ILogger<ApproveChangeProposalCommandHandler> logger,
        TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(dispatchQueue);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(time);

        _store = store;
        _dispatchQueue = dispatchQueue;
        _audit = audit;
        _config = config;
        _logger = logger;
        _time = time;
    }

    /// <inheritdoc />
    public async Task<Result<ChangeProposal>> Handle(
        ApproveChangeProposalCommand request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        return await ChangeProposalCommandHelper.ApplyDecisionAsync(
            _store,
            _audit,
            _config,
            _logger,
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
