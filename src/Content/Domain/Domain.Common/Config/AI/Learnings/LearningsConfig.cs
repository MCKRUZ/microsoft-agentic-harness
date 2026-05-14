namespace Domain.Common.Config.AI.Learnings;

/// <summary>
/// Root configuration for the cross-session learnings subsystem.
/// Bound from <c>AppConfig:AI:Learnings</c> in appsettings.json.
/// </summary>
/// <remarks>
/// Configuration hierarchy:
/// <code>
/// AppConfig.AI.Learnings
/// +-- Enabled                     -- Master toggle for learnings subsystem
/// +-- StoreProvider               -- Keyed DI provider ("graph" or "in_memory")
/// +-- FeedbackAlpha               -- EMA blending weight for feedback in recall
/// +-- FeedbackCeiling             -- Maximum feedback influence on recall score
/// +-- DiversityInjectionRatio     -- Fraction of results replaced by random learnings
/// +-- VolatileShelfLifeDays       -- Decay window for Volatile learnings
/// +-- StableShelfLifeDays         -- Decay window for Stable learnings
/// +-- PruneIntervalHours          -- Background pruning service interval
/// +-- BaselineAdjustmentThreshold -- Min FeedbackWeight for drift baseline adjustment
/// +-- BiasCorrection              -- Bias-corrected EMA for new learnings
/// </code>
/// </remarks>
public class LearningsConfig
{
    /// <summary>
    /// Master toggle. When disabled, all learnings operations return success no-ops.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Keyed DI provider for <c>ILearningsStore</c> ("graph" or "in_memory").
    /// </summary>
    /// <value>Default: "graph"</value>
    public string StoreProvider { get; set; } = "graph";

    /// <summary>
    /// EMA blending weight for feedback in recall scoring formula.
    /// Higher values weight feedback more heavily relative to semantic similarity.
    /// Must be in range (0, 1].
    /// </summary>
    /// <value>Default: 0.25</value>
    public double FeedbackAlpha { get; set; } = 0.25;

    /// <summary>
    /// Maximum influence feedback can exert on final recall score.
    /// Prevents feedback from dominating semantic relevance.
    /// Must be in range (0, 1].
    /// </summary>
    /// <value>Default: 0.3</value>
    public double FeedbackCeiling { get; set; } = 0.3;

    /// <summary>
    /// Fraction of recall results replaced by random non-feedback-optimized learnings.
    /// Prevents filter bubbles. Zero disables diversity injection.
    /// Must be in range [0, 0.5].
    /// </summary>
    /// <value>Default: 0.15</value>
    public double DiversityInjectionRatio { get; set; } = 0.15;

    /// <summary>
    /// Shelf life in days for <c>DecayClass.Volatile</c> learnings.
    /// After this window, freshness decays to zero.
    /// </summary>
    /// <value>Default: 7</value>
    public int VolatileShelfLifeDays { get; set; } = 7;

    /// <summary>
    /// Shelf life in days for <c>DecayClass.Stable</c> learnings.
    /// After this window, freshness decays to zero.
    /// </summary>
    /// <value>Default: 180</value>
    public int StableShelfLifeDays { get; set; } = 180;

    /// <summary>
    /// Interval in hours for the <c>LearningsPruningBackgroundService</c>.
    /// </summary>
    /// <value>Default: 24</value>
    public int PruneIntervalHours { get; set; } = 24;

    /// <summary>
    /// Minimum <c>FeedbackWeight</c> before a learning can trigger drift baseline
    /// recalculation via the learnings-drift bridge.
    /// Must be in range (0, 1].
    /// </summary>
    /// <value>Default: 0.8</value>
    public double BaselineAdjustmentThreshold { get; set; } = 0.8;

    /// <summary>
    /// Whether to apply bias-corrected EMA for new learnings with fewer than 5 updates.
    /// Prevents early observations from dominating the feedback weight.
    /// </summary>
    /// <value>Default: true</value>
    public bool BiasCorrection { get; set; } = true;

    /// <summary>
    /// EMA smoothing factor for temporal decay bias correction.
    /// Controls how aggressively new learnings (UpdateCount &lt; 5) are boosted.
    /// Lower values produce stronger correction for early observations.
    /// Must be in range (0, 1].
    /// </summary>
    /// <value>Default: 0.25</value>
    public double DecayBiasAlpha { get; set; } = 0.25;
}
