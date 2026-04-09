namespace Domain.Common.Config.AI.ContextManagement;

/// <summary>
/// Root configuration for context management including compaction, tool result storage,
/// and budget tracking. Bound from <c>AppConfig:AI:ContextManagement</c> in appsettings.json.
/// </summary>
/// <remarks>
/// <para>
/// Configuration hierarchy:
/// <code>
/// AppConfig.AI.ContextManagement
/// ├── Compaction        — Auto-compact triggers, circuit breaker, strategy limits
/// ├── ToolResultStorage — Disk persistence thresholds for large tool results
/// └── Budget            — Diminishing returns detection and completion thresholds
/// </code>
/// </para>
/// </remarks>
public class ContextManagementConfig
{
    /// <summary>
    /// Gets or sets the compaction configuration controlling auto-compact triggers
    /// and circuit breaker behavior.
    /// </summary>
    public CompactionConfig Compaction { get; set; } = new();

    /// <summary>
    /// Gets or sets the tool result storage configuration for disk persistence
    /// of large tool outputs.
    /// </summary>
    public ToolResultStorageConfig ToolResultStorage { get; set; } = new();

    /// <summary>
    /// Gets or sets the budget configuration for diminishing returns detection
    /// and completion thresholds.
    /// </summary>
    public BudgetConfig Budget { get; set; } = new();
}
