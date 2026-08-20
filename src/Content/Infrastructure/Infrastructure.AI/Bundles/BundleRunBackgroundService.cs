using Application.AI.Common.Interfaces.Bundles;
using Domain.Common.Config;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.Bundles;

/// <summary>
/// Drains the <see cref="IBundleRunDispatchQueue"/> and drives each queued (non-streaming) bundle run to a
/// terminal state through the shared <see cref="IBundleRunExecutor"/>, up to
/// <see cref="Domain.Common.Config.AI.BundleExecution.BundleExecutionConfig.MaxConcurrentDispatchedBundleRuns"/>
/// at once. Mirrors <c>RunDispatchBackgroundService</c>: failure-isolated so one bad run never stalls the
/// queue, and bounded-parallel so one caller's long-running conversation never head-of-lines every other
/// caller's. All the arm-the-ambients-and-run logic lives in the executor, which the streaming endpoint
/// shares — see <see cref="IBundleRunExecutor"/>.
/// </summary>
/// <remarks>
/// <para>
/// Streaming runs are never enqueued (their driver is the stream endpoint), so this service only ever sees
/// poll-only runs.
/// </para>
/// <para>
/// <strong>Safe to run runs side by side because nothing here is what keeps them apart.</strong>
/// <see cref="IBundleRunJobStore.TryBeginRun"/> arms each run exactly once, and
/// <see cref="IBundleRunJobStore.TryCreate"/> already refused, at admission, a second run racing the same
/// conversation or a caller past its concurrent-run cap — the two conditions that would otherwise make
/// parallel dispatch unsafe.
/// </para>
/// </remarks>
public sealed class BundleRunBackgroundService : BackgroundService
{
    private readonly IBundleRunDispatchQueue _queue;
    private readonly IBundleRunExecutor _executor;
    private readonly IOptionsMonitor<AppConfig> _config;
    private readonly ILogger<BundleRunBackgroundService> _logger;

    /// <summary>Initializes a new <see cref="BundleRunBackgroundService"/>.</summary>
    public BundleRunBackgroundService(
        IBundleRunDispatchQueue queue,
        IBundleRunExecutor executor,
        IOptionsMonitor<AppConfig> config,
        ILogger<BundleRunBackgroundService> logger)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(logger);

        _queue = queue;
        _executor = executor;
        _config = config;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The degree is read once, at start. A host that changes it takes effect on restart, which is the
        // honest bound — reshaping a running drain loop mid-flight buys nothing and would make the number
        // of in-flight runs unpredictable. Mirrors RunDispatchBackgroundService.ExecuteAsync exactly.
        var degree = Math.Max(1, _config.CurrentValue.AI.BundleExecution.MaxConcurrentDispatchedBundleRuns);

        try
        {
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
            await _executor.ExecuteAsync(jobId, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutdown — the executor already recorded the run as cancelled.
        }
        catch (Exception ex)
        {
            // The executor records its own failures; this is a last-resort guard so a defect there can
            // never tear down the drain loop and stall every subsequent run.
            _logger.LogError(ex, "Bundle run {JobId} dispatch failed unexpectedly.", jobId);
        }
    }
}
