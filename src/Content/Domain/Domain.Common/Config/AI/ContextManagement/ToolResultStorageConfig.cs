namespace Domain.Common.Config.AI.ContextManagement;

/// <summary>
/// Configuration for disk persistence of large tool results that exceed in-context limits.
/// Bound from <c>AppConfig:AI:ContextManagement:ToolResultStorage</c> in appsettings.json.
/// </summary>
/// <remarks>
/// <para>
/// When a tool result exceeds <see cref="PerResultCharLimit"/>, the full result is persisted
/// to disk and a truncated preview of <see cref="PreviewSizeChars"/> characters is kept in the
/// conversation context with a reference pointer to the full result on disk.
/// </para>
/// </remarks>
public class ToolResultStorageConfig
{
    /// <summary>
    /// Gets or sets the character limit for a single tool result. Results exceeding this
    /// threshold are persisted to disk with only a preview kept in context.
    /// </summary>
    public int PerResultCharLimit { get; set; } = 50000;

    /// <summary>
    /// Gets or sets the aggregate character limit for all tool results within a single message.
    /// When the combined size of tool results in one message exceeds this limit, overflow results
    /// are persisted to disk.
    /// </summary>
    public int AggregatePerMessageCharLimit { get; set; } = 200000;

    /// <summary>
    /// Gets or sets the number of characters retained in the conversation context as a preview
    /// when a tool result is persisted to disk.
    /// </summary>
    public int PreviewSizeChars { get; set; } = 2000;

    /// <summary>
    /// Gets or sets the base directory path for storing persisted tool results.
    /// Relative paths are resolved from the working directory.
    /// </summary>
    public string StoragePath { get; set; } = ".agent-sessions";
}
