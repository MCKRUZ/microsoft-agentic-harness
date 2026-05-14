using Application.AI.Common.Interfaces.Learnings;
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
    private readonly IOptionsMonitor<LearningsConfig> _config;
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
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var interval = TimeSpan.FromHours(_config.CurrentValue.PruneIntervalHours);
            await Task.Delay(interval, stoppingToken);

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
