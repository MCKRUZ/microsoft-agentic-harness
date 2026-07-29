using Application.AI.Common.Interfaces.Runs;
using Domain.AI.Runs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

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
/// </remarks>
public sealed class RunDispatchBackgroundService : BackgroundService
{
    private readonly IRunDispatchQueue _queue;
    private readonly IRunJobStore _store;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _time;
    private readonly ILogger<RunDispatchBackgroundService> _logger;

    /// <summary>Initializes the dispatcher.</summary>
    public RunDispatchBackgroundService(
        IRunDispatchQueue queue,
        IRunJobStore store,
        IServiceScopeFactory scopeFactory,
        TimeProvider time,
        ILogger<RunDispatchBackgroundService> logger)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);

        _queue = queue;
        _store = store;
        _scopeFactory = scopeFactory;
        _time = time;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var jobId in _queue.DequeueAllAsync(stoppingToken).ConfigureAwait(false))
        {
            if (stoppingToken.IsCancellationRequested)
                break;

            try
            {
                await DispatchAsync(jobId, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Reached only when the claim itself failed, so no run was moved to Running and none
                // is left stranded. A failure after the claim is handled inside DispatchAsync, which
                // holds the claimed record and can mark it terminal.
                _logger.LogError(ex, "Run {JobId} could not be dispatched.", jobId);
            }
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

        var outcome = await executor.ExecuteAsync(claimed, stoppingToken).ConfigureAwait(false);

        if (outcome.IsSuccess)
        {
            Finish(claimed, RunStatus.Succeeded, error: null);
            _logger.LogInformation("Run {JobId} ({RunKind}) succeeded.", claimed.JobId, claimed.Kind);
            return;
        }

        // The executor's messages are caller-safe by contract, so they pass through rather than being
        // flattened into "it failed" — a caller learns why its own run did not work.
        var error = string.Join("; ", outcome.Errors);
        Finish(claimed, RunStatus.Failed, error);
        _logger.LogWarning("Run {JobId} ({RunKind}) failed: {Error}", claimed.JobId, claimed.Kind, error);
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
