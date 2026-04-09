using Application.AI.Common.Models.Tools;

namespace Application.AI.Common.Interfaces.Tools;

/// <summary>
/// Executes a batch of tool calls with configurable concurrency.
/// Implementations partition calls by concurrency safety classification
/// and execute read-only tools in parallel while serializing write tools.
/// </summary>
/// <remarks>
/// <para>
/// The strategy is invoked by the agent orchestration loop when the LLM requests
/// multiple tool calls in a single response. Rather than executing them sequentially,
/// the strategy classifies each call and maximizes throughput while preserving safety.
/// </para>
/// <para>
/// <strong>Execution order:</strong>
/// <list type="number">
///   <item>Read-only tools execute in parallel (bounded by <c>StreamingExecutionConfig.ParallelBatchSize</c>)</item>
///   <item>Write-serial tools execute one at a time, in request order</item>
/// </list>
/// Results are always returned in the original request order regardless of execution order.
/// </para>
/// </remarks>
public interface IToolExecutionStrategy
{
    /// <summary>
    /// Executes a batch of tool requests. Read-only tools may run in parallel;
    /// write tools run serially. Results are returned in request order.
    /// </summary>
    /// <param name="requests">The tool execution requests to process.</param>
    /// <param name="progress">Optional progress reporter for streaming UI updates.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Results in the same order as <paramref name="requests"/>.</returns>
    Task<IReadOnlyList<ToolExecutionResult>> ExecuteBatchAsync(
        IReadOnlyList<ToolExecutionRequest> requests,
        IProgress<ToolExecutionProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
