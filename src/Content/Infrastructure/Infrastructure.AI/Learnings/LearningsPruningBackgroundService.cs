using Application.AI.Common.Interfaces.Learnings;
using Application.Common.Helpers;
using Domain.Common;
using Domain.Common.Config.AI.Learnings;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.Learnings;

/// <summary>
/// Background service that periodically prunes expired learnings
/// based on the configured interval.
/// </summary>
public sealed class LearningsPruningBackgroundService : BackgroundService
{
    private readonly ILearningDecayService _decayService;
    private readonly LastKnownGoodSetting<TimeSpan> _interval;
    private readonly ILogger<LearningsPruningBackgroundService> _logger;

    public LearningsPruningBackgroundService(
        ILearningDecayService decayService,
        IOptionsMonitor<LearningsConfig> config,
        ILogger<LearningsPruningBackgroundService> logger)
    {
        ArgumentNullException.ThrowIfNull(decayService);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(logger);

        _decayService = decayService;
        _logger = logger;

        // Read through a last-known-good guard: a hot-reloaded value that fails validation makes
        // CurrentValue throw, and an uncontained throw here would stop the host (#821).
        _interval = new LastKnownGoodSetting<TimeSpan>(
            () => TimeSpan.FromHours(config.CurrentValue.PruneIntervalHours),
            TimeSpan.FromHours(new LearningsConfig().PruneIntervalHours),
            logger,
            "AppConfig:AI:Learnings:PruneIntervalHours");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(_interval.Current, stoppingToken);

            try
            {
                var result = await _decayService.PruneExpiredAsync(stoppingToken);
                if (result.IsSuccess)
                    _logger.LogInformation("Pruning cycle complete: {Count} learnings removed", result.Value);
                else
                    _logger.LogWarning("Pruning cycle failed: {Errors}", string.Join(", ", result.Errors));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error during learnings pruning cycle");
            }
        }
    }

    /// <summary>
    /// Exposed for testability — runs a single pruning cycle.
    /// </summary>
    public Task<Result<int>> PruneNowAsync(CancellationToken ct) => _decayService.PruneExpiredAsync(ct);
}
