namespace Domain.AI.Learnings;

/// <summary>
/// Identifies the origin of a learning entry. Used by <c>DriftEscalationBridge</c>
/// to filter drift-originated learnings and by audit queries for provenance reporting.
/// </summary>
public enum LearningSourceType
{
    /// <summary>A human user explicitly corrected agent output.</summary>
    HumanCorrection = 0,
    /// <summary>Drift detection identified a quality regression and generated a corrective learning.</summary>
    DriftDetection = 1,
    /// <summary>An escalation was resolved with corrections that became a learning.</summary>
    EscalationResolution = 2,
    /// <summary>The agent identified its own mistake and self-corrected.</summary>
    AgentSelfImprovement = 3,
    /// <summary>A learning was manually entered by an operator or admin.</summary>
    ManualEntry = 4
}
