using Domain.AI.Learnings;
using Domain.Common;

namespace Application.AI.Common.Interfaces.Learnings;

/// <summary>
/// Bridge that adjusts drift baselines when high-confidence learnings originating
/// from drift events receive sufficient positive feedback.
/// </summary>
/// <remarks>
/// Called by <c>ImproveLearningCommandHandler</c> after updating a learning's feedback weight.
/// The bridge checks whether the learning meets the criteria for baseline adjustment:
/// <list type="number">
///   <item>Learning's <c>Source.SourceType</c> is <c>DriftDetection</c></item>
///   <item>Learning's <c>FeedbackWeight</c> exceeds <c>LearningsConfig.BaselineAdjustmentThreshold</c></item>
/// </list>
/// If both conditions are met, the bridge triggers a drift baseline update for the affected scope
/// and resolves the originating drift event.
/// </remarks>
public interface ILearningsDriftBridge
{
    /// <summary>
    /// Checks whether the given learning qualifies for drift baseline adjustment
    /// and, if so, orchestrates the update, audit, and resolution.
    /// </summary>
    /// <param name="learning">The learning entry after feedback weight update.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// Success if adjustment was performed or was not needed.
    /// Failure if the baseline update itself failed.
    /// </returns>
    Task<Result> CheckAndAdjustBaselineAsync(LearningEntry learning, CancellationToken ct);
}
