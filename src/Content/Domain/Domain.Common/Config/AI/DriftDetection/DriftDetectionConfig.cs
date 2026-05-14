namespace Domain.Common.Config.AI.DriftDetection;

/// <summary>
/// Configuration for the EWMA-based drift detection subsystem.
/// Bound from <c>AppConfig:AI:DriftDetection</c> in appsettings.json.
/// </summary>
/// <remarks>
/// Configuration hierarchy:
/// <code>
/// AppConfig.AI.DriftDetection
/// +-- Enabled                -- Master toggle for drift detection
/// +-- EwmaLambda             -- EWMA smoothing factor (0, 1]
/// +-- ControlLimitWidth      -- Sigma multiplier for control limits
/// +-- MinSamplesForBaseline  -- Minimum evaluations before baseline is valid
/// +-- BaselineWindowDays     -- Rolling window for baseline recalculation
/// +-- WarnThresholdSigma     -- Deviation triggering Warn severity
/// +-- AlertThresholdSigma    -- Deviation triggering Alert severity
/// +-- EscalateThresholdSigma -- Deviation triggering Escalate severity
/// +-- EscalationEnabled      -- Whether Escalate severity triggers Phase 2 escalation
/// +-- AuditPath              -- Directory for JSONL drift audit files
/// </code>
/// Threshold ordering invariant: Warn &lt; Alert &lt; Escalate.
/// Enforced by <c>DriftDetectionConfigValidator</c>.
/// </remarks>
public class DriftDetectionConfig
{
    /// <summary>
    /// Master toggle. When disabled, <c>DefaultDriftDetectionService</c>
    /// returns <c>Result.Success</c> with default/empty values for all operations.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// EWMA smoothing factor. Higher values weight recent observations more heavily.
    /// Must be in range (0, 1].
    /// </summary>
    /// <value>Default: 0.2</value>
    public double EwmaLambda { get; set; } = 0.2;

    /// <summary>
    /// Sigma multiplier for EWMA control limits (UCL/LCL).
    /// UCL = baseline_mean + L * sigma * sqrt(lambda / (2 - lambda)).
    /// </summary>
    /// <value>Default: 3.0</value>
    public double ControlLimitWidth { get; set; } = 3.0;

    /// <summary>
    /// Minimum number of evaluations required before a baseline is considered valid.
    /// </summary>
    /// <value>Default: 20</value>
    public int MinSamplesForBaseline { get; set; } = 20;

    /// <summary>
    /// Rolling window in days for baseline recalculation.
    /// </summary>
    /// <value>Default: 7</value>
    public int BaselineWindowDays { get; set; } = 7;

    /// <summary>
    /// Sigma deviation threshold for <c>DriftSeverity.Warn</c>.
    /// Must be less than <see cref="AlertThresholdSigma"/>.
    /// </summary>
    /// <value>Default: 1.5</value>
    public double WarnThresholdSigma { get; set; } = 1.5;

    /// <summary>
    /// Sigma deviation threshold for <c>DriftSeverity.Alert</c>.
    /// Must be between <see cref="WarnThresholdSigma"/> and <see cref="EscalateThresholdSigma"/>.
    /// </summary>
    /// <value>Default: 2.5</value>
    public double AlertThresholdSigma { get; set; } = 2.5;

    /// <summary>
    /// Sigma deviation threshold for <c>DriftSeverity.Escalate</c>.
    /// Must be greater than <see cref="AlertThresholdSigma"/>.
    /// </summary>
    /// <value>Default: 3.0</value>
    public double EscalateThresholdSigma { get; set; } = 3.0;

    /// <summary>
    /// Whether drift events at <c>DriftSeverity.Escalate</c> trigger the
    /// Phase 2 human escalation system via <c>IEscalationService</c>.
    /// </summary>
    /// <value>Default: true</value>
    public bool EscalationEnabled { get; set; } = true;

    /// <summary>
    /// Directory path for the JSONL drift audit store.
    /// </summary>
    /// <value>Default: "data/audit"</value>
    public string AuditPath { get; set; } = "data/audit";
}
