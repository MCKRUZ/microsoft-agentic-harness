using Application.AI.Common.Interfaces.Changes;
using Domain.AI.Changes;
using Domain.Common;
using Domain.Common.Config;
using MediatR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Application.AI.Common.CQRS.Changes.CancelChangeProposal;

/// <summary>
/// Handles <see cref="CancelChangeProposalCommand"/>: transitions a non-terminal,
/// non-merging proposal to <see cref="ChangeProposalStatus.Cancelled"/>. Cancel
/// is illegal once <c>Merging</c> has started; that's enforced by the state machine.
/// </summary>
/// <remarks>
/// Cancellation is terminal, and the orchestrator early-returns on terminal proposals,
/// so the audit append performed by
/// <see cref="ChangeProposalCommandHelper.ApplyDecisionAsync"/> is the only durable
/// record this decision will ever get — the in-process store's copy dies with the host.
/// </remarks>
public sealed class CancelChangeProposalCommandHandler
    : IRequestHandler<CancelChangeProposalCommand, Result<ChangeProposal>>
{
    /// <summary>The keyed gate decision identifier used for cancellation history entries.</summary>
    public const string CancellationGateKey = "cancellation";

    private readonly IChangeProposalStore _store;
    private readonly IChangeAuditWriter _audit;
    private readonly IOptionsMonitor<AppConfig> _config;
    private readonly ILogger<CancelChangeProposalCommandHandler> _logger;
    private readonly TimeProvider _time;

    /// <summary>Initializes a new <see cref="CancelChangeProposalCommandHandler"/>.</summary>
    /// <param name="store">The proposal store.</param>
    /// <param name="audit">Durable hash-chained audit sink; the cancellation is appended before the state change is persisted.</param>
    /// <param name="config">Application configuration supplying the pipeline kill switch and orchestrator mode.</param>
    /// <param name="logger">Logger for kill-switch and audit-failure diagnostics.</param>
    /// <param name="time">Clock used to timestamp the gate decision.</param>
    public CancelChangeProposalCommandHandler(
        IChangeProposalStore store,
        IChangeAuditWriter audit,
        IOptionsMonitor<AppConfig> config,
        ILogger<CancelChangeProposalCommandHandler> logger,
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
        CancelChangeProposalCommand request,
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
            statusGuard: p =>
            {
                if (p.IsTerminal)
                {
                    return Result<ChangeProposal>.Conflict(
                        $"Cannot cancel proposal in terminal status {p.Status}.");
                }
                if (p.Status == ChangeProposalStatus.Merging)
                {
                    return Result<ChangeProposal>.Conflict(
                        "Cannot cancel proposal while merge is in progress.");
                }
                return null;
            },
            decisionFactory: () => new GateDecision
            {
                Timestamp = _time.GetUtcNow(),
                GateKey = CancellationGateKey,
                Action = GateAction.Fail,
                Reason = string.IsNullOrEmpty(request.Reason)
                    ? $"cancelled by {request.CancelledBy}"
                    : request.Reason,
                ReviewerId = request.CancelledBy,
                DurationMs = 0
            },
            targetStatus: ChangeProposalStatus.Cancelled,
            postSave: null,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}
