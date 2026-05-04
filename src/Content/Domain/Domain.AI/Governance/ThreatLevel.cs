namespace Domain.AI.Governance;

/// <summary>
/// Severity classification for security threats detected by governance scanners.
/// </summary>
public enum ThreatLevel
{
    /// <summary>No threat detected.</summary>
    None,
    /// <summary>Minimal risk, informational only.</summary>
    Low,
    /// <summary>Moderate risk, should be reviewed.</summary>
    Medium,
    /// <summary>High risk, should be blocked by default.</summary>
    High,
    /// <summary>Critical risk, must be blocked.</summary>
    Critical
}
