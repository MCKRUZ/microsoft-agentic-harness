using Application.AI.Common.Interfaces.Planner;
using Application.AI.Common.Interfaces.Runs;
using Domain.AI.Planner;
using Domain.AI.Runs;
using Domain.Common;
using Domain.Common.Config;
using MediatR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Application.AI.Common.CQRS.Workflows.StartRun;

/// <summary>
/// Handles <see cref="StartWorkflowRunCommand"/>: confirms the caller owns the workflow, bounds how
/// much it may have in flight, records the run, and queues it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The workflow is resolved before anything is queued.</strong> The store is scope-filtered,
/// so a workflow belonging to another caller resolves to nothing and is reported exactly as an
/// unknown one — a caller cannot discover which identifiers exist by trying to run them.
/// </para>
/// <para>
/// <strong>Nothing executes on this thread.</strong> The command returns as soon as the run is
/// recorded and queued, so an HTTP caller is never held open for the length of a workflow. Everything
/// after this point happens on the dispatcher, which is why the run carries its own owner and
/// envelope rather than expecting an ambient caller.
/// </para>
/// <para>
/// <strong>The limits are applied by the store, not here.</strong> Both are read-then-write decisions
/// — is this workflow already running, is this caller at its ceiling — and asking then inserting
/// leaves a window in which concurrent requests all see the limit as unmet. This decides what a
/// refusal <em>means</em> to a caller; the store decides whether one happened.
/// </para>
/// </remarks>
public sealed class StartWorkflowRunCommandHandler
    : IRequestHandler<StartWorkflowRunCommand, Result<StartWorkflowRunResult>>
{
    private readonly IPlanStateStore _planStore;
    private readonly IRunJobStore _runStore;
    private readonly IRunDispatchQueue _queue;
    private readonly IOptionsMonitor<AppConfig> _config;
    private readonly TimeProvider _time;
    private readonly ILogger<StartWorkflowRunCommandHandler> _logger;

    /// <summary>Initializes a new <see cref="StartWorkflowRunCommandHandler"/>.</summary>
    public StartWorkflowRunCommandHandler(
        IPlanStateStore planStore,
        IRunJobStore runStore,
        IRunDispatchQueue queue,
        IOptionsMonitor<AppConfig> config,
        TimeProvider time,
        ILogger<StartWorkflowRunCommandHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(planStore);
        ArgumentNullException.ThrowIfNull(runStore);
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);

        _planStore = planStore;
        _runStore = runStore;
        _queue = queue;
        _config = config;
        _time = time;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Result<StartWorkflowRunResult>> Handle(
        StartWorkflowRunCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var config = _config.CurrentValue.AI.WorkflowSubmission;
        if (!config.Enabled)
        {
            return Result<StartWorkflowRunResult>.Forbidden(
                "Workflow submission is disabled. Set AppConfig.AI.WorkflowSubmission.Enabled = true to enable it.");
        }

        var planId = new PlanId(request.WorkflowId);
        var owned = await _planStore.IsPlanWritableByCallerAsync(planId, cancellationToken).ConfigureAwait(false);
        if (!owned.IsSuccess)
        {
            _logger.LogError(
                "Could not resolve workflow {WorkflowId} while starting a run: {Errors}",
                request.WorkflowId, string.Join("; ", owned.Errors));

            return Result<StartWorkflowRunResult>.Fail("The workflow could not be read.");
        }

        if (!owned.Value)
            return Result<StartWorkflowRunResult>.NotFound($"No workflow {request.WorkflowId} found.");

        var record = new RunRecord
        {
            JobId = Guid.NewGuid().ToString("N"),
            Kind = RunKind.Workflow,
            TargetId = request.WorkflowId.ToString(),
            OwnerId = request.OwnerId,
            TenantId = request.TenantId,
            Envelope = request.Envelope,
            Status = RunStatus.Queued,
            CreatedAt = _time.GetUtcNow()
        };

        // Admission is offered after ownership, so a caller at its cap is never told about the
        // existence of a workflow it does not own.
        var admission = _runStore.TryCreate(record, config.MaxConcurrentRunsPerOwner);
        if (admission != RunAdmission.Accepted)
            return Refuse(admission, request.WorkflowId, config.MaxConcurrentRunsPerOwner);

        // Queued only after the record exists. The other order has a window in which a dispatcher
        // claims a job the store has never heard of, and the run vanishes with the caller holding an
        // identifier that will never resolve.
        //
        // Deliberately NOT the caller's token. Past the admission above the record is committed, and a
        // record that is committed but never queued is never claimed, never finishes, and — because
        // only terminal runs are reclaimed — never goes away: it pins its workflow at 409 and holds one
        // of the owner's slots for the life of the process. A client that disconnects mid-request is
        // ordinary, so that must not be reachable by hanging up. Abandoning the run here would be
        // wrong regardless: the caller was already told it was accepted.
        try
        {
            await _queue.EnqueueAsync(record.JobId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Unreachable with an unbounded in-process channel, which is why the token was the only
            // live cause. Guarded anyway because the queue is the seam for a durable one, where a
            // write genuinely can fail — and the cost of an unguarded failure is not a lost run but a
            // permanently unusable workflow. Recording it terminal releases both holds and tells the
            // caller the truth.
            _logger.LogError(ex, "Run {JobId} was accepted but could not be queued.", record.JobId);
            _runStore.Update(record with
            {
                Status = RunStatus.Failed,
                Error = "The run was accepted but could not be queued for execution.",
                CompletedAt = _time.GetUtcNow()
            });

            return Result<StartWorkflowRunResult>.Fail("The run could not be queued for execution.");
        }

        _logger.LogInformation(
            "Queued run {JobId} for workflow {WorkflowId}.", record.JobId, request.WorkflowId);

        return Result<StartWorkflowRunResult>.Success(new StartWorkflowRunResult { JobId = record.JobId });
    }

    /// <summary>Explains a refused admission in terms the caller can act on.</summary>
    private static Result<StartWorkflowRunResult> Refuse(
        RunAdmission admission, Guid workflowId, int maxConcurrentRuns) => admission switch
    {
        RunAdmission.TargetAlreadyRunning => Result<StartWorkflowRunResult>.Conflict(
            $"Workflow {workflowId} already has a run in progress. A workflow's execution state is "
            + "held against the workflow itself, so a second run would share it with the first. Wait "
            + "for the current run to finish."),

        RunAdmission.OwnerAtCapacity => Result<StartWorkflowRunResult>.ValidationFailure(
            [$"This caller already has {maxConcurrentRuns} run(s) in flight, the maximum this host "
             + "permits. Wait for one to finish before starting another."]),

        _ => Result<StartWorkflowRunResult>.Fail("The run could not be accepted.")
    };
}
