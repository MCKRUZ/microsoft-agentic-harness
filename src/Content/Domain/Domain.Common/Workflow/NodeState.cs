namespace Domain.Common.Workflow;

/// <summary>
/// Represents the state of a single node in a workflow.
///
/// <para><b>Generic Design:</b></para>
/// Works for any node type (phase, skill, activity, gate, etc.) with no hardcoded enums.
/// All domain knowledge comes from AGENT.md and SKILL.md files.
///
/// <para><b>Node Types:</b></para>
/// <list type="bullet">
///   <item><description>"phase" - High-level workflow phase (e.g., "phase0-discovery")</description></item>
///   <item><description>"skill" - Executable skill/activity (e.g., "discovery-intake")</description></item>
///   <item><description>"validation" - Validation gate (e.g., "discovery-validation")</description></item>
///   <item><description>"decision" - Decision point</description></item>
/// </list>
///
/// <para><b>Status Values:</b></para>
/// Defined in AGENT.md state_configuration, common values include:
/// <list type="bullet">
///   <item><description>"not_started" - Node hasn't started yet</description></item>
///   <item><description>"in_progress" - Node is currently executing</description></item>
///   <item><description>"paused" - Node is paused</description></item>
///   <item><description>"awaiting_input" - Waiting for human input</description></item>
///   <item><description>"awaiting_approval" - Waiting for human approval</description></item>
///   <item><description>"completed" - Node finished successfully</description></item>
///   <item><description>"failed" - Node failed</description></item>
///   <item><description>"skipped" - Node was skipped</description></item>
/// </list>
/// </summary>
public class NodeState
{
    /// <summary>
    /// Unique identifier for this node within the workflow.
    /// Examples: "discovery-intake", "stakeholder-discovery", "feasibility-assessment"
    /// </summary>
    public string NodeId { get; set; } = string.Empty;

    /// <summary>
    /// Type of this node, defining its behavior.
    /// Examples: "phase", "skill", "activity", "validation", "decision", "gate"
    /// </summary>
    public string NodeType { get; set; } = "skill";

    /// <summary>
    /// Current status of this node.
    /// Valid values are defined in AGENT.md state_configuration.allowed_statuses
    /// </summary>
    public string Status { get; set; } = "not_started";

    /// <summary>
    /// When this node was started (null if not started).
    /// </summary>
    public DateTime? StartedAt { get; set; }

    /// <summary>
    /// When this node was completed (null if not completed).
    /// </summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>
    /// Number of execution attempts (for retry/review loops).
    /// Starts at 1, increments on each retry.
    /// </summary>
    public int Iteration { get; set; } = 1;

    /// <summary>
    /// Domain-specific metadata for this node.
    /// Common keys include:
    /// <list type="bullet">
    ///   <item><description>"validation_score" - Quality score (0-100)</description></item>
    ///   <item><description>"decision" - Gate decision outcome</description></item>
    ///   <item><description>"token_count" - AI tokens used</description></item>
    ///   <item><description>"duration_seconds" - Execution time</description></item>
    ///   <item><description>"error_message" - Error if failed</description></item>
    ///   <item><description>"critical_issues" - Count of critical issues</description></item>
    ///   <item><description>"high_issues" - Count of high issues</description></item>
    ///   <item><description>"conditions" - Conditions for conditional go</description></item>
    /// </list>
    /// </summary>
    public Dictionary<string, object> Metadata { get; set; } = new();

    /// <summary>
    /// Gets metadata value by key.
    /// </summary>
    public T? GetMetadata<T>(string key, T? defaultValue = default)
    {
        if (!Metadata.TryGetValue(key, out var value))
            return defaultValue;

        if (value is T typed)
            return typed;

        // Try to convert
        try
        {
            return (T)Convert.ChangeType(value, typeof(T));
        }
        catch
        {
            return defaultValue;
        }
    }

    /// <summary>
    /// Sets metadata value by key.
    /// </summary>
    public void SetMetadata<T>(string key, T value)
    {
        Metadata[key] = value!;
    }

    /// <summary>
    /// Checks if this node is complete.
    /// </summary>
    public bool IsComplete() => Status == "completed";

    /// <summary>
    /// Checks if this node is currently active (in progress).
    /// </summary>
    public bool IsActive() => Status == "in_progress" || Status == "awaiting_input" || Status == "awaiting_approval";

    /// <summary>
    /// Checks if this node has failed.
    /// </summary>
    public bool HasFailed() => Status == "failed";

    /// <summary>
    /// Gets the duration of this node's execution.
    /// Returns null if not completed, TimeSpan from StartedAt to CompletedAt otherwise.
    /// </summary>
    public TimeSpan? GetDuration()
    {
        if (StartedAt == null)
            return null;

        var end = CompletedAt ?? DateTime.UtcNow;
        return end - StartedAt.Value;
    }

    /// <summary>
    /// Increments the iteration count.
    /// </summary>
    public void IncrementIteration() => Iteration++;
}
