using Application.AI.Common.Interfaces.Learnings;
using Domain.AI.Learnings;
using Domain.Common;
using Domain.Common.Config.AI.Learnings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.Learnings;

/// <summary>
/// Calculates temporal freshness scores for learnings based on their decay class
/// and optionally applies EMA bias correction for low-sample learnings.
/// </summary>
/// <remarks>
/// <para>Freshness formula (linear decay): <c>clamp(1 - age_days / shelf_life_days, 0, 1)</c></para>
/// <para>Bias correction (EMA warm-up): <c>1 / (1 - (1 - alpha)^n)</c> where alpha = <see cref="LearningsConfig.DecayBiasAlpha"/>
/// and n = <see cref="LearningEntry.UpdateCount"/>. Applied only when <c>BiasCorrection</c> is enabled and <c>0 &lt; n &lt; 5</c>.</para>
/// </remarks>
public sealed class DefaultLearningDecayService : ILearningDecayService
{
    private readonly ILearningsStore _store;
    private readonly IOptionsMonitor<LearningsConfig> _config;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<DefaultLearningDecayService> _logger;

    public DefaultLearningDecayService(
        ILearningsStore store,
        IOptionsMonitor<LearningsConfig> config,
        TimeProvider timeProvider,
        ILogger<DefaultLearningDecayService> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _store = store;
        _config = config;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<double> CalculateFreshnessAsync(LearningEntry learning, CancellationToken ct)
    {
        if (learning.DecayClass == DecayClass.Permanent)
            return Task.FromResult(1.0);

        var config = _config.CurrentValue;
        var shelfLifeDays = learning.DecayClass switch
        {
            DecayClass.Volatile => config.VolatileShelfLifeDays,
            DecayClass.Stable => config.StableShelfLifeDays,
            _ => config.StableShelfLifeDays
        };

        if (shelfLifeDays <= 0)
            return Task.FromResult(0.0);

        var referenceTime = learning.LastReinforcedAt ?? learning.CreatedAt;
        var ageDays = (_timeProvider.GetUtcNow() - referenceTime).TotalDays;
        var rawFreshness = Math.Clamp(1.0 - (ageDays / shelfLifeDays), 0.0, 1.0);

        if (config.BiasCorrection && learning.UpdateCount is > 0 and < 5)
        {
            var correctionFactor = 1.0 / (1.0 - Math.Pow(1.0 - config.DecayBiasAlpha, learning.UpdateCount));
            return Task.FromResult(Math.Clamp(rawFreshness * correctionFactor, 0.0, 1.0));
        }

        return Task.FromResult(rawFreshness);
    }

    /// <inheritdoc />
    public async Task<Result<int>> PruneExpiredAsync(CancellationToken ct)
    {
        var criteria = new LearningSearchCriteria();

        var searchResult = await _store.SearchAsync(criteria, ct);
        if (!searchResult.IsSuccess)
            return Result<int>.Fail(searchResult.Errors.ToArray());

        var prunedCount = 0;

        foreach (var learning in searchResult.Value!)
        {
            if (learning.DecayClass == DecayClass.Permanent || learning.IsDeleted)
                continue;

            var freshness = await CalculateFreshnessAsync(learning, ct);
            if (freshness <= 0.0)
            {
                var deleteResult = await _store.SoftDeleteAsync(learning.LearningId, "Expired by decay service", ct);
                if (deleteResult.IsSuccess)
                    prunedCount++;
                else
                    _logger.LogWarning("Failed to prune learning {LearningId}: {Errors}",
                        learning.LearningId, string.Join(", ", deleteResult.Errors));
            }
        }

        _logger.LogInformation("Pruned {Count} expired learnings", prunedCount);
        return Result<int>.Success(prunedCount);
    }
}
