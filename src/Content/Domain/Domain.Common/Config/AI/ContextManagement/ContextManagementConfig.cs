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
/// ├── Budget            — Diminishing returns detection and completion thresholds
/// └── PromptComposition — Authoritative section-based static system-prompt builder (off by default)
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

    /// <summary>
    /// Gets or sets the prompt-composition configuration controlling whether the authoritative
    /// section-based composer builds the agent's static system prompt. Off by default — when
    /// enabled, the identity + skill-instructions + permission-rules sections are assembled within
    /// a token budget in place of the legacy verbatim merged instruction.
    /// </summary>
    public PromptCompositionConfig PromptComposition { get; set; } = new();
}
