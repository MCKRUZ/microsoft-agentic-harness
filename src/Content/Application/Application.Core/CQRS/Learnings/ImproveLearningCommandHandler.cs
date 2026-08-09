using Application.AI.Common.Interfaces.KnowledgeGraph;
using Application.AI.Common.Interfaces.Learnings;
using Application.AI.Common.OpenTelemetry.Metrics;
using Domain.AI.KnowledgeGraph.Models;
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
/// <para>
/// <strong>This handler can replace a learning's content, so it gates too (issue #338).</strong>
/// <see cref="ImproveLearningCommand.ReinforcementContent"/> overwrites the stored text, which
/// <c>LearningsRecallContextProvider</c> later replays into an agent's instructions — so it is a
/// write of new content in every sense that matters, and classifying it only at creation would let
/// a caller launder text past the gate by remembering something benign and improving it into
/// something else. The trust marker is re-derived from the new content rather than inherited: an
/// entry that was trusted when written must be able to become quarantined, and the reverse.
/// </para>
/// <para>
/// Both handlers call the same <see cref="IMemoryWriteGate"/>; the classification ladder itself
/// still lives in exactly one place. <c>LearningsWriteGateCoverageTests</c> fails if a third
/// handler ever writes learning content without consulting it.
/// </para>
/// </remarks>
public sealed class ImproveLearningCommandHandler : IRequestHandler<ImproveLearningCommand, Result<LearningEntry>>
{
    private readonly ILearningsStore _store;
    private readonly ILearningsDriftBridge _driftBridge;
    private readonly IMemoryWriteGate _writeGate;
    private readonly IOptionsMonitor<AppConfig> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ImproveLearningCommandHandler> _logger;

    /// <summary>Initializes a new instance of the <see cref="ImproveLearningCommandHandler"/> class.</summary>
    /// <param name="store">Durable learnings persistence.</param>
    /// <param name="driftBridge">Checks whether the improved learning shifts a drift baseline.</param>
    /// <param name="writeGate">Classifies replacement content before it is persisted. Required —
    /// see the remarks on <see cref="ImproveLearningCommandHandler"/>.</param>
    /// <param name="options">Application configuration; supplies the learnings enablement flag.</param>
    /// <param name="timeProvider">Clock used for the reinforcement timestamp.</param>
    /// <param name="logger">Logger for gate decisions and bridge failures.</param>
    public ImproveLearningCommandHandler(
        ILearningsStore store,
        ILearningsDriftBridge driftBridge,
        IMemoryWriteGate writeGate,
        IOptionsMonitor<AppConfig> options,
        TimeProvider timeProvider,
        ILogger<ImproveLearningCommandHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(writeGate);

        _store = store;
        _driftBridge = driftBridge;
        _writeGate = writeGate;
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

        // Only a content replacement needs classifying. A pure feedback update re-persists text the
        // gate already saw, so scanning it again would buy nothing and would put a scan (and, when
        // the optional intent check is on, an LLM call) on the per-recall access-reinforcement path.
        var trust = learning.Trust;
        if (request.ReinforcementContent is not null)
        {
            var decision = await _writeGate.EvaluateAsync(
                LearningsWriteGateContract.KeyFor(learning.Scope, learning.Category),
                request.ReinforcementContent,
                LearningsWriteGateContract.EntityType,
                cancellationToken);

            if (!decision.Persist)
            {
                _logger.LogWarning(
                    "Learning {LearningId} content replacement blocked: {Reason}",
                    learning.LearningId, decision.Reason);

                return Result<LearningEntry>.Fail(LearningsWriteGateContract.RejectedErrorCode);
            }

            // Re-derived, never inherited: the stored text is being replaced, so the old
            // classification describes content that will no longer exist.
            trust = decision.Trust;
        }

        var updated = learning with
        {
            FeedbackWeight = newWeight,
            UpdateCount = learning.UpdateCount + 1,
            LastReinforcedAt = _timeProvider.GetUtcNow(),
            Content = request.ReinforcementContent ?? learning.Content,
            Trust = trust
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
