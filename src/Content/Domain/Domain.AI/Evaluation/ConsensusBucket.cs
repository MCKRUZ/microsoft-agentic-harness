namespace Domain.AI.Evaluation;

/// <summary>
/// How much a panel of LLM judges (a "jury") agreed when scoring one case.
/// </summary>
/// <remarks>
/// <para>
/// Derived from the spread (max − min) of the panelists' individual scores by
/// <c>JuryAggregator</c>, using the configurable <c>ConsensusMaxSpread</c> /
/// <c>ConflictMinSpread</c> thresholds. It is advisory metadata for a human reviewer —
/// the case's pass/fail <see cref="Verdict"/> is still decided by comparing the
/// aggregated (median) score to the metric threshold, independent of this bucket.
/// </para>
/// <para>
/// Lives in Domain (not Application) because <see cref="MetricScore"/> carries it as a
/// first-class field and Domain types cannot reference Application. The richer
/// per-panelist breakdown stays in the Application layer.
/// </para>
/// <para>
/// <c>null</c> on a <see cref="MetricScore"/> means the score came from a single judge
/// (no panel configured), not that the panel agreed.
/// </para>
/// </remarks>
public enum ConsensusBucket
{
    /// <summary>The panelists' scores cluster tightly (spread ≤ <c>ConsensusMaxSpread</c>): they agree.</summary>
    Consensus,

    /// <summary>Moderate disagreement — neither tight consensus nor outright conflict.</summary>
    Split,

    /// <summary>The panelists' scores diverge widely (spread ≥ <c>ConflictMinSpread</c>): a human should look.</summary>
    Conflict
}
