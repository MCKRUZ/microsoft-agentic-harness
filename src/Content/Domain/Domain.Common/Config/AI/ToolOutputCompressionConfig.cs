namespace Domain.Common.Config.AI;

/// <summary>
/// Configuration for the tool output compression subsystem.
/// Bound from <c>AppConfig.AI.ToolOutputCompression</c>.
/// </summary>
public sealed class ToolOutputCompressionConfig
{
    /// <summary>Master toggle. When false, all outputs pass through uncompressed.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Default token threshold that triggers compression. Outputs below this
    /// pass through untouched. Individual tools can override via
    /// <c>ITool.CompressionTokenThreshold</c>.
    /// </summary>
    public int DefaultTokenThreshold { get; set; } = 2000;

    /// <summary>
    /// Whether to use an LLM for summarization when heuristic strategies
    /// produce a result that still exceeds the threshold on FreeText content.
    /// </summary>
    public bool LlmFallbackEnabled { get; set; } = true;

    /// <summary>Timeout in seconds for LLM fallback calls.</summary>
    public int LlmFallbackTimeoutSeconds { get; set; } = 5;

    /// <summary>
    /// Operation name passed to <c>IModelRouter.RouteOperationAsync</c>
    /// to resolve the economy-tier model for LLM compression.
    /// </summary>
    public string LlmRoutingOperation { get; set; } = "output_compression";
}
