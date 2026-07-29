using Application.AI.Common.Interfaces.KnowledgeGraph;
using Application.AI.Common.Interfaces.Runs;
using Domain.AI.Runs;
using Domain.Common.Config;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.Runs;

/// <summary>
/// Drains the run queue, claims each run exactly once, resolves the executor for its kind, and records
/// how it ended.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The dispatcher owns the outcome, not the executors.</strong> Each
/// <see cref="IRunKindExecutor"/> reports success or failure and this records it. Leaving executors to
/// write their own terminal state would mean every kind reimplemented the same thing, and the one
/// that threw before writing would leave a run stuck at <see cref="RunStatus.Running"/> forever, with
/// a caller polling it indefinitely.
/// </para>
/// <para>
/// <strong>An unregistered kind fails the run rather than the dispatcher.</strong> A missing executor
/// is a wiring gap, and taking the whole loop down for it would stop every other kind of work in the
/// host — turning one mis-registration into a total outage.
/// </para>
/// <para>
/// <strong>The caller's knowledge scope is re-established per job, here rather than in each
/// executor.</strong> Scope is ambient (<c>AsyncLocal</c>) and set at an HTTP entry point; it does not
/// survive the hop onto this thread. Every run carries the owner and tenant that authorized it for
/// exactly this reason, and re-arming them is a property of dispatching any run, not of any one kind
/// — leaving it to executors means the next kind added inherits the bug. The token is disposed per
/// job, so one caller's identity can never stay ambient for the next.
/// </para>
/// <para>
/// A run whose scope cannot be established is <em>failed</em>, never executed. Running unscoped is not
/// a degraded mode: an absent owner reads as a global record, so the work would silently see and
/// touch every caller's data.
/// </para>
/// <para>
/// <strong>Two things here rest on both seams being in-process, and stop being true when either is
/// replaced.</strong> A job taken off the queue is lost if the host stops before its body runs, leaving
/// its record <c>Queued</c> — harmless only because an in-memory store dies with the process, and
/// because a durable queue would redeliver an unacknowledged message. Pair a durable store with a
/// non-redelivering queue and that record is stranded: never claimed, never terminal, never reclaimed,
/// pinning its workflow and holding one of its owner's slots. Likewise a run that ends by throwing
/// <see cref="OperationCanceledException"/> for any reason other than host shutdown is recorded
/// Failed rather than Cancelled, losing a distinction <see cref="RunStatus"/> exists to make; today
/// no executor does that, because <c>IPlanRunExecutor</c> reports cancellation as a result.
/// </para>
/// </remarks>
public sealed class RunDispatchBackgroundService : BackgroundService
{
    private readonly IRunDispatchQueue _queue;
    private readonly IRunJobStore _store;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptionsMonitor<AppConfig> _config;
    private readonly TimeProvider _time;
    private readonly ILogger<RunDispatchBackgroundService> _logger;

    /// <summary>Initializes the dispatcher.</summary>
    public RunDispatchBackgroundService(
        IRunDispatchQueue queue,
        IRunJobStore store,
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<AppConfig> config,
        TimeProvider time,
        ILogger<RunDispatchBackgroundService> logger)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);

        _queue = queue;
        _store = store;
        _scopeFactory = scopeFactory;
        _config = config;
        _time = time;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Bounded parallelism rather than one-at-a-time. Awaiting each run to completion before
        // dequeuing the next makes host-wide throughput exactly one, so any caller's long workflow
        // delays every other caller's — and it makes the per-owner cap read as a concurrency
        // allowance the host never honours.
        //
        // Safe to run runs side by side because nothing here is what keeps them apart: TryBeginRun
        // arms each run exactly once, and admission permits only one live run per workflow, so two
        // dispatched runs can never be the same work nor share one plan's execution state.
        //
        // The degree is read once, at start. A host that changes it takes effect on restart, which is
        // the honest bound — reshaping a running drain loop mid-flight buys nothing and would make
        // the number of in-flight runs unpredictable.
        var degree = Math.Max(1, _config.CurrentValue.AI.WorkflowSubmission.MaxConcurrentDispatchedRuns);

        try
        {
            // Parallel.ForEachAsync already is this loop: it bounds concurrency, keeps pulling as slots
            // free, and does not return until every started item has finished — that last part being
            // the one that matters at shutdown, since each in-flight run holds a claimed record only
            // its own dispatch can move out of Running.
            //
            // Its one sharp edge is that a body which throws tears down the whole loop.
            // DispatchGuardedAsync is written never to, so this ends only when the queue completes or
            // the host stops.
            await Parallel.ForEachAsync(
                _queue.DequeueAllAsync(stoppingToken),
                new ParallelOptions { MaxDegreeOfParallelism = degree, CancellationToken = stoppingToken },
                async (jobId, token) => await DispatchGuardedAsync(jobId, token).ConfigureAwait(false))
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutdown.
        }
    }

    /// <summary>Dispatches one run, never letting a failure escape into the drain loop.</summary>
    private async Task DispatchGuardedAsync(string jobId, CancellationToken stoppingToken)
    {
        try
        {
            await DispatchAsync(jobId, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutdown. DispatchAsync has already recorded the run as cancelled if it had claimed
            // one, so there is nothing left to record here.
        }
        catch (Exception ex)
        {
            // Reached only when the claim itself failed, so no run was moved to Running and none is
            // left stranded. A failure after the claim is handled inside DispatchAsync, which holds
            // the claimed record and can mark it terminal.
            _logger.LogError(ex, "Run {JobId} could not be dispatched.", jobId);
        }
    }

    private async Task DispatchAsync(string jobId, CancellationToken stoppingToken)
    {
        var claimed = _store.TryBeginRun(jobId, _time.GetUtcNow());
        if (claimed is null)
        {
            // Already claimed, already finished, or swept. All three mean there is nothing to do, and
            // none of them is an error: the claim is exactly what makes redelivery harmless.
            _logger.LogDebug("Run {JobId} was not claimable; skipping.", jobId);
            return;
        }

        // Everything past the claim is guarded, because the run is now Running and only this method
        // can move it out of that state. An escape here would leave it Running forever, with a caller
        // polling a run nothing will ever finish.
        try
        {
            await ExecuteClaimedAsync(claimed, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutdown, not a fault of the run. Recorded as cancelled so an operator can tell it
            // apart from work that broke, and so it is terminal rather than stranded at Running.
            Finish(claimed, RunStatus.Cancelled, "The host shut down before the run completed.");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Run {JobId} ({RunKind}) threw.", claimed.JobId, claimed.Kind);
            Finish(claimed, RunStatus.Failed, "The run failed unexpectedly.");
        }
    }

    private async Task ExecuteClaimedAsync(RunRecord claimed, CancellationToken stoppingToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();

        var executor = scope.ServiceProvider.GetKeyedService<IRunKindExecutor>(claimed.Kind);
        if (executor is null)
        {
            _logger.LogError(
                "Run {JobId} declares kind {RunKind}, which has no registered executor.",
                claimed.JobId, claimed.Kind);

            Finish(claimed, RunStatus.Failed, "This host cannot execute that kind of run.");
            return;
        }

        // Required, not optional. A host that has not registered the scope writer cannot tell this
        // work who it is acting as, and the work would run as nobody — which reads as everybody.
        var scopeWriter = scope.ServiceProvider.GetService<IKnowledgeScopeWriter>();
        if (scopeWriter is null)
        {
            _logger.LogError(
                "Run {JobId} cannot execute: this host registers no {Writer}, so the run's identity "
                + "cannot be established and it must not run unscoped.",
                claimed.JobId, nameof(IKnowledgeScopeWriter));

            Finish(claimed, RunStatus.Failed, "This host cannot establish the run's identity.");
            return;
        }

        // Disposed before the next job is claimed, restoring whatever was ambient before. Without the
        // restore, job N's owner stays ambient for job N+1 and the second run executes as the first
        // caller.
        using var identity = scopeWriter.SetScope(claimed.OwnerId, claimed.TenantId);

        var outcome = await executor.ExecuteAsync(claimed, stoppingToken).ConfigureAwait(false);

        if (!outcome.IsSuccess || outcome.Value is null)
        {
            // The executor could not run the work. Its messages are caller-safe by contract, so they
            // pass through rather than being flattened into "it failed" — a caller learns why its own
            // run did not work.
            var error = outcome.Errors.Count > 0
                ? string.Join("; ", outcome.Errors)
                : "The run reported no outcome.";

            Finish(claimed, RunStatus.Failed, error);
            _logger.LogWarning("Run {JobId} ({RunKind}) failed: {Error}", claimed.JobId, claimed.Kind, error);
            return;
        }

        var completion = outcome.Value;
        Finish(claimed, completion.Status, completion.Detail);
        _logger.LogInformation(
            "Run {JobId} ({RunKind}) ended {RunStatus}.", claimed.JobId, claimed.Kind, completion.Status);
    }

    /// <summary>
    /// Writes a claimed run's terminal state. Takes the claimed record rather than re-reading it: the
    /// run is Running by this point, so it can no longer be re-claimed, and the dispatcher holds no
    /// caller identity to read it back with.
    /// </summary>
    private void Finish(RunRecord claimed, RunStatus status, string? error) =>
        _store.Update(claimed with
        {
            Status = status,
            Error = error,
            CompletedAt = _time.GetUtcNow()
        });
}
