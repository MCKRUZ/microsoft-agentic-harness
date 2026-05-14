namespace Domain.AI.DriftDetection;

/// <summary>
/// Tiered severity levels for detected drift, driving escalation behavior.
/// Integer values encode ordering so that <c>DriftSeverity.Warn &lt; DriftSeverity.Alert</c> is valid.
/// </summary>
public enum DriftSeverity
{
    /// <summary>No significant drift detected.</summary>
    None = 0,
    /// <summary>Drift exceeds warning threshold. Logged and notified.</summary>
    Warn = 1,
    /// <summary>Drift exceeds alert threshold. Requires attention.</summary>
    Alert = 2,
    /// <summary>Drift exceeds escalation threshold. Triggers Phase 2 escalation if enabled.</summary>
    Escalate = 3
}
