using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.Changes;
using Domain.AI.Changes;
using Domain.Common;
using Domain.Common.Config;
using MediatR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Application.AI.Common.CQRS.Changes.SubmitChangeProposal;

/// <summary>
/// Handles <see cref="SubmitChangeProposalCommand"/>: resolves required gates, derives
/// the deterministic id, persists the proposal in Draft, and (when the pipeline is
/// Enabled) inline-drives the orchestrator so the proposal lands at AwaitingApproval,
/// Merged, or Rejected before the command returns. Idempotent — a duplicate submission
/// within the same id-bucket returns the prior proposal verbatim instead of creating
/// a parallel pipeline.
/// </summary>
public sealed class SubmitChangeProposalCommandHandler
    : IRequestHandler<SubmitChangeProposalCommand, Result<ChangeProposal>>
{
    private readonly IChangeProposalStore _store;
    private readonly IChangeProposalGateResolver _gateResolver;
    private readonly IChangeProposalOrchestrator _orchestrator;
    private readonly IAgentExecutionContext _agentContext;
    private readonly IOptionsMonitor<AppConfig> _config;
    private readonly TimeProvider _time;
    private readonly ILogger<SubmitChangeProposalCommandHandler> _logger;

    /// <summary>Initializes a new <see cref="SubmitChangeProposalCommandHandler"/>.</summary>
    public SubmitChangeProposalCommandHandler(
        IChangeProposalStore store,
        IChangeProposalGateResolver gateResolver,
        IChangeProposalOrchestrator orchestrator,
        IAgentExecutionContext agentContext,
        IOptionsMonitor<AppConfig> config,
        TimeProvider time,
        ILogger<SubmitChangeProposalCommandHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(gateResolver);
        ArgumentNullException.ThrowIfNull(orchestrator);
        ArgumentNullException.ThrowIfNull(agentContext);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);

        _store = store;
        _gateResolver = gateResolver;
        _orchestrator = orchestrator;
        _agentContext = agentContext;
        _config = config;
        _time = time;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Result<ChangeProposal>> Handle(
        SubmitChangeProposalCommand request,
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

        var identity = _agentContext.AgentIdentity;
        if (identity is null)
        {
            return Result<ChangeProposal>.Unauthorized(
                "SubmitChangeProposalCommand requires an ambient agent identity. " +
                "Caller must execute inside an agent scope established by AgentIdentityResolutionBehavior.");
        }

        var submittedAt = request.SubmittedAt ?? _time.GetUtcNow();
        var gates = request.RequiredGates ?? _gateResolver.Resolve(request.Target.Kind, request.BlastRadius);

        if (gates.Count == 0)
        {
            return Result<ChangeProposal>.Fail(
                "IChangeProposalGateResolver returned an empty gate list — proposals must have at least one gate.");
        }

        var proposal = ChangeProposal.Create(
            target: request.Target,
            diff: request.Diff,
            submittedBy: identity,
            summary: request.Summary,
            blastRadius: request.BlastRadius,
            requiredGates: gates,
            submittedAt: submittedAt);

        var existing = await _store.GetAsync(proposal.Id, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            _logger.LogInformation(
                "Idempotent ChangeProposal re-submission ignored (proposal {ProposalId} already exists in status {Status}).",
                existing.Id,
                existing.Status);
            return Result<ChangeProposal>.Success(existing);
        }

        await _store.SaveAsync(proposal, cancellationToken).ConfigureAwait(false);

        // Drive the orchestrator inline so the command returns the post-pipeline
        // proposal (Validating-deferred, AwaitingApproval, Approved, Merging,
        // Merged, or Rejected depending on what the gates decide).
        var mode = ParseMode(changesConfig.DefaultMode);
        var processed = await _orchestrator.ProcessAsync(proposal.Id, mode, cancellationToken).ConfigureAwait(false);
        return Result<ChangeProposal>.Success(processed ?? proposal);
    }

    private static OrchestratorMode ParseMode(string raw) =>
        Enum.TryParse<OrchestratorMode>(raw, ignoreCase: true, out var mode) ? mode : OrchestratorMode.Shadow;
}
