using Application.AI.Common.Evaluation.Models;
using Application.AI.Common.Interfaces.Evaluation;
using Application.AI.Common.Interfaces.Runs;
using Domain.AI.Runs;
using Domain.Common;
using Domain.Common.Config;
using MediatR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Application.Core.CQRS.Evaluation.Runs;

/// <summary>
/// Handles <see cref="StartEvalRunCommand"/>: resolves the named datasets, records the run and its
/// submission, and queues it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Names are resolved before anything is queued.</strong> A run accepted against a dataset that
/// does not exist would be a 202 followed by a failure the caller has to poll for — and it would have
/// consumed one of their concurrency slots to say so. Resolving first turns that into an immediate,
/// actionable answer.
/// </para>
/// <para>
/// <strong>Nothing executes on this thread.</strong> The command returns as soon as the run is recorded
/// and queued, so an HTTP caller is never held open for the length of a suite — which, at the default
/// ceilings, is hundreds of governed agent turns.
/// </para>
/// </remarks>
public sealed class StartEvalRunCommandHandler
    : IRequestHandler<StartEvalRunCommand, Result<StartEvalRunResult>>
{
    private readonly IEvalDatasetCatalog _catalog;
    private readonly IEvalRunSubmissionStore _submissions;
    private readonly IRunJobStore _runStore;
    private readonly IRunDispatchQueue _queue;
    private readonly IOptionsMonitor<AppConfig> _config;
    private readonly TimeProvider _time;
    private readonly ILogger<StartEvalRunCommandHandler> _logger;

    /// <summary>Initializes a new <see cref="StartEvalRunCommandHandler"/>.</summary>
    public StartEvalRunCommandHandler(
        IEvalDatasetCatalog catalog,
        IEvalRunSubmissionStore submissions,
        IRunJobStore runStore,
        IRunDispatchQueue queue,
        IOptionsMonitor<AppConfig> config,
        TimeProvider time,
        ILogger<StartEvalRunCommandHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(submissions);
        ArgumentNullException.ThrowIfNull(runStore);
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);

        _catalog = catalog;
        _submissions = submissions;
        _runStore = runStore;
        _queue = queue;
        _config = config;
        _time = time;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Result<StartEvalRunResult>> Handle(
        StartEvalRunCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var config = _config.CurrentValue;
        if (!config.AI.Evaluation.Enabled)
        {
            return Result<StartEvalRunResult>.Forbidden(
                "Evaluation is disabled. Set AppConfig.AI.Evaluation.Enabled = true, and configure "
                + "AppConfig.AI.Evaluation.DatasetRoots, to enable it.");
        }

        // Resolved before anything is queued, so a bad name costs an immediate answer rather than a
        // 202 followed by a failure the caller has to poll for — having spent a concurrency slot to
        // say so. The paths are discarded here: what matters at admission is only that every name
        // resolves. The executor resolves again when it runs, which is what catches a dataset removed
        // in between.
        var resolution = _catalog.Resolve(request.DatasetNames);
        if (!resolution.IsComplete)
            return Result<StartEvalRunResult>.NotFound($"No dataset named '{resolution.MissingName}' is available.");

        var jobId = Guid.NewGuid().ToString("N");

        var record = new RunRecord
        {
            JobId = jobId,
            Kind = RunKind.Evaluation,

            // The run IS the target. Deliberate, and not a placeholder: admission refuses a second live
            // run against the same target, which for a workflow protects one shared plan state machine.
            // Evaluations share no such state — two callers evaluating one dataset are two independent
            // reads — so a target derived from the datasets would serialize unrelated work and let one
            // caller's long suite lock everybody else out of it. A per-run target makes the check
            // correctly inert here rather than switching it off somewhere it also governs workflows.
            TargetId = jobId,
            OwnerId = request.OwnerId,
            TenantId = request.TenantId,
            Envelope = request.Envelope,
            Status = RunStatus.Queued,
            CreatedAt = _time.GetUtcNow()
        };

        // Admission and insertion in one atomic step, in the store, for the same reason the workflow
        // path does it there: "is this caller at its ceiling" is a read-then-write decision, and asking
        // here then inserting leaves a window in which concurrent requests all see the limit as unmet.
        //
        // The ceiling is the run substrate's, not evaluation's, and the store counts every kind of run
        // one caller has in flight. Its configuration lives under WorkflowSubmission because that is
        // where the substrate was introduced — as does RunRecordTtl, which the store also reads. The
        // section name reads narrower than what it governs; the alternative, a second per-owner ceiling
        // applied to the same cross-kind counter, would mean the limit that bound a caller depended on
        // which endpoint they happened to call last.
        var admission = _runStore.TryCreate(record, config.AI.WorkflowSubmission.MaxConcurrentRunsPerOwner);
        if (admission != RunAdmission.Accepted)
            return Refuse(admission, config.AI.WorkflowSubmission.MaxConcurrentRunsPerOwner);

        return await ArmAsync(record, request).ConfigureAwait(false);
    }

    /// <summary>
    /// Stores the admitted run's request and hands it to the dispatcher, undoing the admission if
    /// either step fails.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Submission first, queue second.</strong> The submission's lifetime is the record's — the
    /// same sweep reclaims both — so storing it before admission would leave an entry nothing ever
    /// collects whenever admission refuses. Storing it after the queue would be worse: a dispatcher can
    /// claim the run the instant it is enqueued, and would find no request to execute.
    /// </para>
    /// <para>
    /// Separate from <see cref="Handle"/> because everything above it decides <em>whether</em> the run
    /// happens and everything here makes it happen — and because both failure paths share one
    /// non-obvious obligation, which is easier to state once: a run that is committed but never queued
    /// is never claimed, never finishes, and (only terminal runs being reclaimed) never released.
    /// </para>
    /// </remarks>
    private async Task<Result<StartEvalRunResult>> ArmAsync(RunRecord record, StartEvalRunCommand request)
    {
        try
        {
            _submissions.Add(new EvalRunSubmission
            {
                JobId = record.JobId,
                DatasetNames = request.DatasetNames,
                Options = request.Options
            });
        }
        catch (InvalidOperationException ex)
        {
            // Unreachable with a freshly minted job id, and guarded anyway — the cost of an unguarded
            // failure is not a lost run but one of the caller's slots held for the life of the process.
            _logger.LogError(ex, "Run {JobId} was admitted but its submission could not be stored.", record.JobId);
            Abandon(record, "The run was accepted but its request could not be stored.");

            return Result<StartEvalRunResult>.Fail("The run could not be accepted.");
        }

        // Deliberately NOT the caller's token: past admission the record is committed, and a client
        // that hangs up mid-request is ordinary — stranding a run must not be reachable by hanging up.
        try
        {
            await _queue.EnqueueAsync(record.JobId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Run {JobId} was accepted but could not be queued.", record.JobId);
            Abandon(record, "The run was accepted but could not be queued for execution.");

            return Result<StartEvalRunResult>.Fail("The run could not be queued for execution.");
        }

        _logger.LogInformation(
            "Queued evaluation run {JobId} over {DatasetCount} dataset(s).",
            record.JobId, request.DatasetNames.Count);

        return Result<StartEvalRunResult>.Success(new StartEvalRunResult { JobId = record.JobId });
    }

    /// <summary>
    /// Records an admitted run as failed when it could not be set up to execute.
    /// </summary>
    /// <remarks>
    /// Failed rather than deleted, and the submission is left alone. The record is what the sweeper
    /// reclaims, and it only reclaims terminal runs — so making this one terminal is what eventually
    /// releases both it and the submission keyed by the same id. Removing either by hand here would
    /// split a lifetime that is deliberately shared.
    /// </remarks>
    private void Abandon(RunRecord record, string error) =>
        _runStore.Update(record with
        {
            Status = RunStatus.Failed,
            Error = error,
            CompletedAt = _time.GetUtcNow()
        });

    /// <summary>Explains a refused admission in terms the caller can act on.</summary>
    /// <remarks>
    /// <see cref="RunAdmission.TargetAlreadyRunning"/> is unreachable here — the target is this run's
    /// own freshly-minted id — so it falls to the generic arm rather than being given a message that
    /// describes a conflict evaluations cannot have.
    /// </remarks>
    private static Result<StartEvalRunResult> Refuse(RunAdmission admission, int maxConcurrentRuns) =>
        admission switch
        {
            RunAdmission.OwnerAtCapacity => Result<StartEvalRunResult>.ValidationFailure(
                [$"This caller already has {maxConcurrentRuns} run(s) in flight, the maximum this host "
                 + "permits. Wait for one to finish before starting another."]),

            _ => Result<StartEvalRunResult>.Fail("The run could not be accepted.")
        };
}
