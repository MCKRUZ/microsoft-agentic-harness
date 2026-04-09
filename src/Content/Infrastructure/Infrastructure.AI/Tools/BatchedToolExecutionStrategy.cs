using Application.AI.Common.Interfaces.Tools;
using Application.AI.Common.Models.Tools;
using Domain.AI.Models;
using Domain.AI.Tools;
using Domain.Common.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.Tools;

/// <summary>
/// Executes batched tool calls with concurrency-aware scheduling.
/// Read-only tools run in parallel (bounded by <c>StreamingExecutionConfig.ParallelBatchSize</c>);
/// write-serial tools run one at a time in request order.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Execution flow:</strong>
/// <list type="number">
///   <item>Classify each request via <see cref="IToolConcurrencyClassifier"/></item>
///   <item>Execute all read-only requests in parallel using <see cref="SemaphoreSlim"/> throttling</item>
///   <item>Execute all write-serial requests sequentially</item>
///   <item>Return results in original request order</item>
/// </list>
/// </para>
/// <para>
/// All exceptions are caught and converted to failed <see cref="ToolResult"/> instances.
/// No single tool failure propagates to the batch — callers always receive results for every request.
/// </para>
/// </remarks>
public sealed class BatchedToolExecutionStrategy : IToolExecutionStrategy
{
    private readonly IToolConcurrencyClassifier _classifier;
    private readonly IOptionsMonitor<AppConfig> _options;
    private readonly ILogger<BatchedToolExecutionStrategy> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="BatchedToolExecutionStrategy"/> class.
    /// </summary>
    /// <param name="classifier">Classifies tool concurrency safety.</param>
    /// <param name="options">Application configuration for parallel batch size.</param>
    /// <param name="logger">Logger for execution diagnostics.</param>
    public BatchedToolExecutionStrategy(
        IToolConcurrencyClassifier classifier,
        IOptionsMonitor<AppConfig> options,
        ILogger<BatchedToolExecutionStrategy> logger)
    {
        ArgumentNullException.ThrowIfNull(classifier);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _classifier = classifier;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ToolExecutionResult>> ExecuteBatchAsync(
        IReadOnlyList<ToolExecutionRequest> requests,
        IProgress<ToolExecutionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requests);

        if (requests.Count == 0)
            return [];

        var results = new ToolExecutionResult[requests.Count];
        var readOnlyIndices = new List<int>();
        var writeSerialIndices = new List<int>();

        // Partition requests by concurrency classification
        for (var i = 0; i < requests.Count; i++)
        {
            var classification = _classifier.Classify(requests[i].Tool);
            if (classification == ToolConcurrencyClassification.ReadOnly)
                readOnlyIndices.Add(i);
            else
                writeSerialIndices.Add(i);
        }

        _logger.LogDebug(
            "Batch of {Total} tool calls: {ReadOnly} read-only (parallel), {WriteSerial} write-serial (sequential)",
            requests.Count, readOnlyIndices.Count, writeSerialIndices.Count);

        // Execute read-only tools in parallel with bounded concurrency
        if (readOnlyIndices.Count > 0)
        {
            var batchSize = _options.CurrentValue.AI.Orchestration.StreamingExecution.ParallelBatchSize;
            using var semaphore = new SemaphoreSlim(batchSize, batchSize);

            var tasks = readOnlyIndices.Select(index => ExecuteWithSemaphoreAsync(
                semaphore, requests[index], index, results, progress, cancellationToken));

            await Task.WhenAll(tasks);
        }

        // Execute write-serial tools sequentially
        foreach (var index in writeSerialIndices)
        {
            results[index] = await ExecuteSingleAsync(requests[index], progress, cancellationToken);
        }

        return results;
    }

    private async Task ExecuteWithSemaphoreAsync(
        SemaphoreSlim semaphore,
        ToolExecutionRequest request,
        int index,
        ToolExecutionResult[] results,
        IProgress<ToolExecutionProgress>? progress,
        CancellationToken cancellationToken)
    {
        await semaphore.WaitAsync(cancellationToken);
        try
        {
            results[index] = await ExecuteSingleAsync(request, progress, cancellationToken);
        }
        finally
        {
            semaphore.Release();
        }
    }

    private async Task<ToolExecutionResult> ExecuteSingleAsync(
        ToolExecutionRequest request,
        IProgress<ToolExecutionProgress>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report(new ToolExecutionProgress
        {
            CallId = request.CallId,
            Status = "executing",
            PercentComplete = 0.0
        });

        try
        {
            var result = await request.Tool.ExecuteAsync(
                request.Operation, request.Parameters, cancellationToken);

            progress?.Report(new ToolExecutionProgress
            {
                CallId = request.CallId,
                Status = "completed",
                PercentComplete = 1.0
            });

            return new ToolExecutionResult
            {
                CallId = request.CallId,
                Result = result,
                Completed = true
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            var errorCategory = ToolErrorClassifier.Classify(ex);

            _logger.LogWarning(ex,
                "Tool '{ToolName}' operation '{Operation}' (call {CallId}) failed with category '{ErrorCategory}'",
                request.Tool.Name, request.Operation, request.CallId, errorCategory);

            progress?.Report(new ToolExecutionProgress
            {
                CallId = request.CallId,
                Status = "failed",
                PercentComplete = 1.0
            });

            return new ToolExecutionResult
            {
                CallId = request.CallId,
                Result = ToolResult.Fail($"Tool execution failed: {ex.Message}"),
                Completed = false,
                ErrorCategory = errorCategory
            };
        }
    }
}
