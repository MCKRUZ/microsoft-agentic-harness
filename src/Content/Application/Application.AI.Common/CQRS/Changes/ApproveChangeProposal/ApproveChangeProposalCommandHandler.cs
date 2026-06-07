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

        var proposal = await _store.GetAsync(request.ProposalId, cancellationToken).ConfigureAwait(false);
        if (proposal is null)
        {
            return Result<ChangeProposal>.NotFound(
                $"ChangeProposal '{request.ProposalId}' not found.");
        }

        if (proposal.Status != ChangeProposalStatus.AwaitingApproval)
        {
            return Result<ChangeProposal>.Fail(
                $"Cannot approve proposal in status {proposal.Status} (must be AwaitingApproval).");
        }

        var decision = new GateDecision
        {
            Timestamp = _time.GetUtcNow(),
            GateKey = ApprovalGateKey,
            Action = GateAction.Pass,
            Reason = string.IsNullOrEmpty(request.Reason) ? "approved" : request.Reason,
            ReviewerId = request.ReviewerId,
            DurationMs = 0
        };

        var approved = proposal.TransitionTo(ChangeProposalStatus.Approved, decision);
        await _store.SaveAsync(approved, cancellationToken).ConfigureAwait(false);

        // Drive the orchestrator inline so Approved → Merging → Merged (or
        // Rejected on apply failure) happens before the command returns.
        var mode = ParseMode(changesConfig.DefaultMode);
        var processed = await _orchestrator.ProcessAsync(approved.Id, mode, cancellationToken).ConfigureAwait(false);
        return Result<ChangeProposal>.Success(processed ?? approved);
    }

    private static OrchestratorMode ParseMode(string raw) =>
        Enum.TryParse<OrchestratorMode>(raw, ignoreCase: true, out var mode) ? mode : OrchestratorMode.Shadow;
}
