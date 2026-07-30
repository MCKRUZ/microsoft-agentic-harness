using Application.AI.Common.Interfaces.Escalation;
using Application.AI.Common.Interfaces.Planner;
using Application.AI.Common.Interfaces.Runs;
using Domain.AI.Planner;
using Domain.AI.Runs;
using Domain.Common;
using Domain.Common.Config;
using MediatR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Application.AI.Common.CQRS.Workflows.CancelRun;

/// <summary>
/// Handles <see cref="CancelWorkflowRunCommand"/>: stops a run and withdraws the approvals it was
/// waiting on.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Withdrawing the approval is the point, not a tidy-up.</strong> A gate that outlives its run
/// is a request in a person's queue that can no longer do anything: answering it approves work that
/// will never happen, and the approver has no way to know that. Worse, leaving it pending means a
/// later run of the same workflow — which resumes the same persisted plan — would reconcile against a
/// verdict given for a run that no longer exists.
/// </para>
/// <para>
/// <strong>The run is stopped before its approvals are withdrawn, deliberately.</strong> Withdrawing
/// first produces a resolved escalation while the run is still parked on it, which is exactly what the
/// resume check looks for — so the run would be put back to work in the moment between the two steps,
/// by the cancellation itself.
/// </para>
/// <para>
/// <strong>A run already executing is signalled, not rewritten.</strong> Its terminal state is owned by
/// the dispatch in flight; writing one here would be overwritten by it. There is a narrow window —
/// between a run being claimed and its plan registering for cancellation — in which the signal reaches
/// nothing and the run continues. The caller sees this the same way it sees every other outcome, by
/// reading the run, which is why this reports whether the run had actually stopped rather than
/// claiming it had.
/// </para>
/// </remarks>
public sealed class CancelWorkflowRunCommandHandler
    : IRequestHandler<CancelWorkflowRunCommand, Result<CancelWorkflowRunResult>>
{
    private readonly IRunJobStore _runStore;
    private readonly IRunProgressBroker _progress;
    private readonly IPlanRunCancellationRegistry _cancellationRegistry;
    private readonly IEscalationService _escalations;
    private readonly IOptionsMonitor<AppConfig> _config;
    private readonly TimeProvider _time;
    private readonly ILogger<CancelWorkflowRunCommandHandler> _logger;

    /// <summary>Initializes a new <see cref="CancelWorkflowRunCommandHandler"/>.</summary>
    public CancelWorkflowRunCommandHandler(
        IRunJobStore runStore,
        IRunProgressBroker progress,
        IPlanRunCancellationRegistry cancellationRegistry,
        IEscalationService escalations,
        IOptionsMonitor<AppConfig> config,
        TimeProvider time,
        ILogger<CancelWorkflowRunCommandHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(runStore);
        ArgumentNullException.ThrowIfNull(progress);
        ArgumentNullException.ThrowIfNull(cancellationRegistry);
        ArgumentNullException.ThrowIfNull(escalations);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);

        _runStore = runStore;
        _progress = progress;
        _cancellationRegistry = cancellationRegistry;
        _escalations = escalations;
        _config = config;
        _time = time;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Result<CancelWorkflowRunResult>> Handle(
        CancelWorkflowRunCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_config.CurrentValue.AI.WorkflowSubmission.Enabled)
        {
            return Result<CancelWorkflowRunResult>.Forbidden(
                "Workflow submission is disabled. Set AppConfig.AI.WorkflowSubmission.Enabled = true to enable it.");
        }

        var record = _runStore.Get(request.JobId, request.OwnerId, request.TenantId);

        // Someone else's run, a run that never existed, and a run reached through the wrong workflow's
        // route are one answer — the same rule reading a run follows, and for the same reason: a
        // distinguishable response would let a caller discover work it was not given the id of, and
        // here it would also let them stop it.
        if (record is null
            || record.Kind != RunKind.Workflow
            || !string.Equals(record.TargetId, request.WorkflowId.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            return Result<CancelWorkflowRunResult>.NotFound($"No run {request.JobId} found.");
        }

        if (record.IsTerminal)
        {
            return Result<CancelWorkflowRunResult>.Conflict(
                $"Run {request.JobId} has already finished and cannot be cancelled.");
        }

        var stopped = _runStore.TryCancel(request.JobId, _time.GetUtcNow());
        if (stopped is null)
        {
            // Executing, or it reached a terminal state between the read above and here. Either way its
            // status belongs to the dispatch that holds it, so all that is left is to ask it to stop.
            _cancellationRegistry.TryCancel(new PlanId(request.WorkflowId));

            return Result<CancelWorkflowRunResult>.Success(new CancelWorkflowRunResult
            {
                StoppedImmediately = false,
                WithdrawnApprovals = 0
            });
        }

        // Published here because nothing else will: no dispatch is going to run for this job, and the
        // terminal event is what ends a watcher's stream. Without it, anyone streaming a queued run
        // holds the connection and a stream slot until they give up.
        _progress.Publish(
            stopped.JobId,
            RunProgressKind.RunFinished,
            status: nameof(RunStatus.Cancelled),
            detail: "The run was cancelled by its owner.");

        var withdrawn = await ApprovalWithdrawal.WithdrawAsync(
            _escalations,
            _logger,
            stopped,
            "The run this approval was raised for was cancelled.",

            // The caller's approver-shaped identity where the host could establish one. This value
            // lands beside approver names in the escalation audit, so an owner id would read
            // differently from every other row there — still attributable, but not comparable.
            request.CancellerApproverName ?? request.OwnerId,

            // Deliberately NOT the request's token. The run is already cancelled by this point, so a
            // caller that hangs up mid-withdrawal would leave the rest of its approvals pending with
            // nothing to retry them — the parked-run ceiling only looks at parked runs, and this one is
            // terminal. The same shape as a run being queued on its caller's abort token, which stranded
            // workflows permanently.
            CancellationToken.None).ConfigureAwait(false);

        return Result<CancelWorkflowRunResult>.Success(new CancelWorkflowRunResult
        {
            StoppedImmediately = true,
            WithdrawnApprovals = withdrawn
        });
    }

}
