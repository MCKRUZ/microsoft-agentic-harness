namespace Domain.Common.Config.AI.Orchestration;

/// <summary>
/// Configuration for batched parallel tool execution and progress reporting
/// during streaming agent responses. Bound from <c>AppConfig:AI:Orchestration:StreamingExecution</c>
/// in appsettings.json.
/// </summary>
/// <remarks>
/// <para>
/// When the LLM requests multiple tool calls in a single response, the execution engine
/// batches them into groups of <see cref="ParallelBatchSize"/> and executes each batch
/// concurrently. Progress is reported at <see cref="ProgressCallbackIntervalMs"/> intervals.
/// </para>
/// </remarks>
public class StreamingExecutionConfig
{
    /// <summary>
    /// Gets or sets the maximum number of tool invocations to execute in parallel
    /// within a single batch.
    /// </summary>
    public int ParallelBatchSize { get; set; } = 5;

    /// <summary>
    /// Gets or sets the interval in milliseconds between progress callback invocations
    /// during long-running tool executions.
    /// </summary>
    public int ProgressCallbackIntervalMs { get; set; } = 500;
}
