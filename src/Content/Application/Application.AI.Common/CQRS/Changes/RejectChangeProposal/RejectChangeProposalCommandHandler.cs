using Application.AI.Common.CQRS.Changes.ApproveChangeProposal;
using Application.AI.Common.Interfaces.Changes;
using Domain.AI.Changes;
using Domain.Common;
using Domain.Common.Config;
using MediatR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Application.AI.Common.CQRS.Changes.RejectChangeProposal;

/// <summary>
/// Handles <see cref="RejectChangeProposalCommand"/>: load, transition to
/// <see cref="ChangeProposalStatus.Rejected"/>, append the decision to the durable
/// audit chain and the gate history, persist. Recorded under the same
/// <c>approval</c> gate key as approvals so dashboards can group both decisions by
/// gate.
/// </summary>
/// <remarks>
/// Rejection is terminal, and the orchestrator early-returns on terminal proposals,
/// so the audit append performed by
/// <see cref="ChangeProposalCommandHelper.ApplyDecisionAsync"/> is the only durable
/// record this decision will ever get — the in-process store's copy dies with the host.
/// </remarks>
public sealed class RejectChangeProposalCommandHandler
    : IRequestHandler<RejectChangeProposalCommand, Result<ChangeProposal>>
{
    private readonly IChangeProposalStore _store;
    private readonly IChangeAuditWriter _audit;
    private readonly IOptionsMonitor<AppConfig> _config;
    private readonly ILogger<RejectChangeProposalCommandHandler> _logger;
    private readonly TimeProvider _time;

    /// <summary>Initializes a new <see cref="RejectChangeProposalCommandHandler"/>.</summary>
    /// <param name="store">The proposal store.</param>
    /// <param name="audit">Durable hash-chained audit sink; the rejection is appended before the state change is persisted.</param>
    /// <param name="config">Application configuration supplying the pipeline kill switch and orchestrator mode.</param>
    /// <param name="logger">Logger for kill-switch and audit-failure diagnostics.</param>
    /// <param name="time">Clock used to timestamp the gate decision.</param>
    public RejectChangeProposalCommandHandler(
        IChangeProposalStore store,
        IChangeAuditWriter audit,
        IOptionsMonitor<AppConfig> config,
        ILogger<RejectChangeProposalCommandHandler> logger,
        TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(time);

        _store = store;
        _audit = audit;
        _config = config;
        _logger = logger;
        _time = time;
    }

    /// <inheritdoc />
    public async Task<Result<ChangeProposal>> Handle(
        RejectChangeProposalCommand request,
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
                    $"Cannot reject proposal in status {p.Status} (must be AwaitingApproval).")
                : null,
            decisionFactory: () => new GateDecision
            {
                Timestamp = _time.GetUtcNow(),
                GateKey = ApproveChangeProposalCommandHandler.ApprovalGateKey,
                Action = GateAction.Fail,
                Reason = request.Reason,
                ReviewerId = request.ReviewerId,
                DurationMs = 0
            },
            targetStatus: ChangeProposalStatus.Rejected,
            postSave: null,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}
