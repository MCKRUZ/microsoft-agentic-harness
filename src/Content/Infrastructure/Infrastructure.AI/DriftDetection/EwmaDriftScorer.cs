using Application.AI.Common.Interfaces.DriftDetection;
using Domain.AI.DriftDetection;
using Domain.Common;
using Domain.Common.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.DriftDetection;

/// <summary>
/// EWMA-based drift scorer that detects quality regressions using
/// Exponentially Weighted Moving Average control charts.
/// Formula: EWMA_t = λ * x_t + (1 - λ) * EWMA_{t-1}
/// Deviation: |EWMA - baseline_mean| / sigma (in sigma units).
/// </summary>
public sealed class EwmaDriftScorer : IDriftScorer
{
    private readonly IEwmaStateStore _stateStore;
    private readonly IOptionsMonitor<AppConfig> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<EwmaDriftScorer> _logger;

    public EwmaDriftScorer(
        IEwmaStateStore stateStore,
        IOptionsMonitor<AppConfig> options,
        TimeProvider timeProvider,
        ILogger<EwmaDriftScorer> logger)
    {
        _stateStore = stateStore;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Result<DriftDimensionScore>> ScoreDimensionAsync(
        DriftDimension dimension, double currentValue, DriftBaseline baseline, CancellationToken ct)
    {
        var config = _options.CurrentValue.AI.DriftDetection;

        if (!config.Enabled)
        {
            return Result<DriftDimensionScore>.Success(new DriftDimensionScore
            {
                CurrentValue = currentValue,
                BaselineValue = 0.0,
                EwmaValue = 0.0,
                Deviation = 0.0
            });
        }

        if (!baseline.Dimensions.TryGetValue(dimension, out var baselineMean))
            return Result<DriftDimensionScore>.Fail($"Dimension {dimension} not found in baseline.");

        var sigma = baseline.DimensionSigmas.TryGetValue(dimension, out var s) ? s : 0.0;

        var stateResult = await _stateStore.GetStateAsync(
            baseline.Scope, baseline.ScopeIdentifier, dimension, ct);

        if (!stateResult.IsSuccess)
            return Result<DriftDimensionScore>.Fail(stateResult.Errors.ToArray());

        var existingState = stateResult.Value;
        var lambda = config.EwmaLambda;

        var previousEwma = existingState?.CurrentEwma ?? baselineMean;
        var newEwma = lambda * currentValue + (1 - lambda) * previousEwma;
        var deviation = sigma > 0 ? Math.Abs(newEwma - baselineMean) / sigma : 0.0;

        var newSampleCount = (existingState?.SampleCount ?? 0) + 1;

        var updatedState = new EwmaState
        {
            Scope = baseline.Scope,
            ScopeIdentifier = baseline.ScopeIdentifier,
            Dimension = dimension,
            CurrentEwma = newEwma,
            SampleCount = newSampleCount,
            LastUpdatedAt = _timeProvider.GetUtcNow()
        };

        var saveResult = await _stateStore.SaveStateAsync(updatedState, ct);
        if (!saveResult.IsSuccess)
            return Result<DriftDimensionScore>.Fail(saveResult.Errors.ToArray());

        if (sigma == 0.0)
        {
            _logger.LogDebug(
                "Zero variance for {Dimension} in {Scope}:{Identifier} — deviation forced to 0",
                dimension, baseline.Scope, baseline.ScopeIdentifier);
        }

        return Result<DriftDimensionScore>.Success(new DriftDimensionScore
        {
            CurrentValue = currentValue,
            BaselineValue = baselineMean,
            EwmaValue = newEwma,
            Deviation = deviation
        });
    }
}
