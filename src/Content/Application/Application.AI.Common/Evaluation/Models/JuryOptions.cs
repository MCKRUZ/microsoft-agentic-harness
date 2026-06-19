namespace Application.AI.Common.Evaluation.Models;

/// <summary>
/// How a panelist's scores are reduced to the single aggregate score the metric
/// thresholds against.
/// </summary>
public enum JuryScoreAggregation
{
    /// <summary>Middle value — robust to a single outlier judge (default, recommended).</summary>
    Median,

    /// <summary>Arithmetic mean — every panelist pulls the score, outliers included.</summary>
    Mean,

    /// <summary>Lowest score wins — conservative; one skeptical panelist can fail the case.</summary>
    Min
}

/// <summary>
/// Configures the judge panel ("jury") for LLM-judge evaluations. Bind via
/// <c>services.Configure&lt;JuryOptions&gt;(...)</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Off by default.</b> When <see cref="Panelists"/> is empty, the jury behaves
/// exactly as a single judge (using the configured <c>JudgeOptions</c>) — no extra
/// model calls, no behavior change. A panel only activates when a consumer populates it.
/// </para>
/// <para>
/// A panel of N panelists runs N judge calls per case (in parallel, so latency stays
/// ~1×, but spend is N×). Recommended on the scheduled eval suite, not per-PR.
/// </para>
/// </remarks>
public sealed class JuryOptions
{
    /// <summary>
    /// The panel members. Empty ⇒ single-judge behavior (the default). One entry behaves
    /// like a single judge that may wear a persona.
    /// </summary>
    public IList<JuryPanelistSpec> Panelists { get; set; } = new List<JuryPanelistSpec>();

    /// <summary>How the responding panelists' scores reduce to one aggregate. Default <see cref="JuryScoreAggregation.Median"/>.</summary>
    public JuryScoreAggregation ScoreAggregation { get; set; } = JuryScoreAggregation.Median;

    /// <summary>
    /// Spread at or below which the panel is bucketed <c>Consensus</c> (they agree).
    /// Default <c>0.2</c>. Must be ≤ <see cref="ConflictMinSpread"/>.
    /// </summary>
    public double ConsensusMaxSpread { get; set; } = 0.2;

    /// <summary>
    /// Spread at or above which the panel is bucketed <c>Conflict</c> (a human should look).
    /// Default <c>0.5</c>. Between the two thresholds is <c>Split</c>.
    /// </summary>
    public double ConflictMinSpread { get; set; } = 0.5;
}
