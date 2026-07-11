using Application.AI.Common.Interfaces.Bundles;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Bundles;

/// <summary>
/// Drains the <see cref="IBundleRunDispatchQueue"/> and drives each queued (non-streaming) bundle run to a
/// terminal state through the shared <see cref="IBundleRunExecutor"/>. Mirrors <c>ChangeProposalBackgroundService</c>:
/// failure-isolated so one bad run never stalls the queue. All the arm-the-ambients-and-run logic lives in the
/// executor, which the streaming endpoint shares — see <see cref="IBundleRunExecutor"/>.
/// </summary>
/// <remarks>
/// Streaming runs are never enqueued (their driver is the stream endpoint), so this service only ever sees
/// poll-only runs.
/// </remarks>
public sealed class BundleRunBackgroundService : BackgroundService
{
    private readonly IBundleRunDispatchQueue _queue;
    private readonly IBundleRunExecutor _executor;
    private readonly ILogger<BundleRunBackgroundService> _logger;

    /// <summary>Initializes a new <see cref="BundleRunBackgroundService"/>.</summary>
    public BundleRunBackgroundService(
        IBundleRunDispatchQueue queue,
        IBundleRunExecutor executor,
        ILogger<BundleRunBackgroundService> logger)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(logger);

        _queue = queue;
        _executor = executor;
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
                await _executor.ExecuteAsync(jobId, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break; // host shutdown — the executor already recorded the run as cancelled
            }
            catch (Exception ex)
            {
                // The executor records its own failures; this is a last-resort guard so a defect there can
                // never tear down the drain loop and stall every subsequent run.
                _logger.LogError(ex, "Bundle run {JobId} dispatch failed unexpectedly.", jobId);
            }
        }
    }
}
