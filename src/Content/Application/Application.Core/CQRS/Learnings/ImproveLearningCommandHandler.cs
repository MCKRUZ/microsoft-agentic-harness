using Application.AI.Common.Interfaces.Learnings;
using Application.AI.Common.OpenTelemetry.Metrics;
using Domain.AI.Learnings;
using Domain.Common;
using Domain.Common.Config;
using MediatR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Application.Core.CQRS.Learnings;

/// <summary>
/// Updates a learning's feedback weight using exponential moving average (EMA)
/// with optional bias correction for early updates.
/// </summary>
/// <remarks>
/// <para>EMA formula: <c>newWeight = alpha * normalized + (1 - alpha) * currentWeight</c></para>
/// <para>Bias correction (first 5 updates): <c>corrected = weight / (1 - (1 - alpha)^updateCount)</c></para>
/// <para>After a successful weight update, the <see cref="ILearningsDriftBridge"/> checks whether
/// the learning qualifies for drift baseline adjustment. Bridge failure is non-critical —
/// the learning update always succeeds independently.</para>
/// </remarks>
public sealed class ImproveLearningCommandHandler : IRequestHandler<ImproveLearningCommand, Result<LearningEntry>>
{
    private readonly ILearningsStore _store;
    private readonly ILearningsDriftBridge _driftBridge;
    private readonly IOptionsMonitor<AppConfig> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ImproveLearningCommandHandler> _logger;

    /// <summary>Initializes a new instance of the <see cref="ImproveLearningCommandHandler"/> class.</summary>
    public ImproveLearningCommandHandler(
        ILearningsStore store,
        ILearningsDriftBridge driftBridge,
        IOptionsMonitor<AppConfig> options,
        TimeProvider timeProvider,
        ILogger<ImproveLearningCommandHandler> logger)
    {
        _store = store;
        _driftBridge = driftBridge;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Result<LearningEntry>> Handle(
        ImproveLearningCommand request, CancellationToken cancellationToken)
    {
        var config = _options.CurrentValue.AI.Learnings;
        if (!config.Enabled)
        {
            _logger.LogDebug("Learnings subsystem disabled, skipping improve");
            return Result<LearningEntry>.Success(CreateDisabledPlaceholder(request.LearningId));
        }

        var getResult = await _store.GetAsync(request.LearningId, cancellationToken);
        if (!getResult.IsSuccess || getResult.Value is null)
            return Result<LearningEntry>.NotFound($"Learning {request.LearningId} not found");

        var learning = getResult.Value;
        var alpha = config.FeedbackAlpha;
        var normalized = (request.FeedbackScore - 1.0) / 4.0;
        var newWeight = alpha * normalized + (1 - alpha) * learning.FeedbackWeight;

        if (config.BiasCorrection && learning.UpdateCount < 5)
        {
            var correctionFactor = 1.0 / (1.0 - Math.Pow(1.0 - alpha, learning.UpdateCount + 1));
            newWeight = Math.Clamp(newWeight * correctionFactor, 0.0, 1.0);
        }

        var updated = learning with
        {
            FeedbackWeight = newWeight,
            UpdateCount = learning.UpdateCount + 1,
            LastReinforcedAt = _timeProvider.GetUtcNow(),
            Content = request.ReinforcementContent ?? learning.Content
        };

        var updateResult = await _store.UpdateAsync(updated, cancellationToken);
        if (!updateResult.IsSuccess)
            return Result<LearningEntry>.Fail(updateResult.Errors.ToArray());

        var bridgeResult = await _driftBridge.CheckAndAdjustBaselineAsync(updated, cancellationToken);
        if (!bridgeResult.IsSuccess)
        {
            _logger.LogWarning(
                "Drift baseline adjustment failed for learning {LearningId}: {Reason}",
                updated.LearningId, string.Join("; ", bridgeResult.Errors));
        }

        LearningsMetrics.Improved.Add(1);
        return Result<LearningEntry>.Success(updated);
    }

    private LearningEntry CreateDisabledPlaceholder(Guid learningId) => new()
    {
        LearningId = learningId,
        Category = LearningCategory.FactualCorrection,
        DecayClass = DecayClass.Volatile,
        Scope = new LearningScope { IsGlobal = true },
        Content = string.Empty,
        Source = new LearningSource
        {
            SourceType = LearningSourceType.ManualEntry,
            SourceId = string.Empty,
            SourceDescription = "Disabled no-op"
        },
        Provenance = new LearningProvenance
        {
            OriginPipeline = "disabled",
            OriginTask = "disabled",
            OriginTimestamp = _timeProvider.GetUtcNow(),
            Confidence = 0
        },
        CreatedAt = _timeProvider.GetUtcNow()
    };
}
