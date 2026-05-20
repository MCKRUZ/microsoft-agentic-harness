using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Application.AI.Common.Interfaces.Planner;
using Application.AI.Common.Interfaces.RAG;
using Domain.AI.Planner;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Planner.StepExecutors;

/// <summary>
/// Executes RAG retrieval steps within a plan by delegating to <see cref="IRagOrchestrator"/>
/// for single-source queries or <see cref="IMultiSourceOrchestrator"/> for multi-source
/// fan-out across vector, graph, and web sources. Tracks retrieval cost via
/// <see cref="IRetrievalCostTracker"/> and serializes the assembled context as JSON output
/// for downstream plan steps.
/// </summary>
public sealed class RetrievalPlanStepExecutor : IPlanStepExecutor
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    private readonly IRagOrchestrator _ragOrchestrator;
    private readonly IMultiSourceOrchestrator _multiSourceOrchestrator;
    private readonly IQueryComplexityClassifier _complexityClassifier;
    private readonly IRetrievalCostTracker _costTracker;
    private readonly IPlanProgressNotifier _notifier;
    private readonly PlanExecutionContext _executionContext;
    private readonly ILogger<RetrievalPlanStepExecutor> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="RetrievalPlanStepExecutor"/>.
    /// </summary>
    /// <param name="ragOrchestrator">Single-source RAG pipeline orchestrator.</param>
    /// <param name="multiSourceOrchestrator">Multi-source orchestrator for fan-out retrieval.</param>
    /// <param name="complexityClassifier">Query complexity classifier for multi-source routing.</param>
    /// <param name="costTracker">Tracks token usage and latency per retrieval call.</param>
    /// <param name="notifier">Plan progress notifier for real-time status updates.</param>
    /// <param name="executionContext">Current plan execution context with depth tracking.</param>
    /// <param name="logger">Logger instance.</param>
    public RetrievalPlanStepExecutor(
        IRagOrchestrator ragOrchestrator,
        IMultiSourceOrchestrator multiSourceOrchestrator,
        IQueryComplexityClassifier complexityClassifier,
        IRetrievalCostTracker costTracker,
        IPlanProgressNotifier notifier,
        PlanExecutionContext executionContext,
        ILogger<RetrievalPlanStepExecutor> logger)
    {
        _ragOrchestrator = ragOrchestrator;
        _multiSourceOrchestrator = multiSourceOrchestrator;
        _complexityClassifier = complexityClassifier;
        _costTracker = costTracker;
        _notifier = notifier;
        _executionContext = executionContext;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<StepExecutionResult> ExecuteAsync(
        PlanStep step,
        IReadOnlyDictionary<PlanStepId, string> upstreamOutputs,
        CancellationToken ct)
    {
        if (step.Configuration is not RetrievalStepConfiguration config)
        {
            return new StepExecutionResult
            {
                Status = StepExecutionStatus.Failed,
                Duration = TimeSpan.Zero,
                ErrorMessage = $"Step '{step.Name}' has invalid configuration type for Retrieval executor."
            };
        }

        var sw = Stopwatch.StartNew();

        try
        {
            var query = ResolveQuery(config.Query, upstreamOutputs);
            string outputJson;

            if (config.UseMultiSource)
                outputJson = await ExecuteMultiSourceAsync(query, config, ct);
            else
                outputJson = await ExecuteSingleSourceAsync(query, config, ct);

            sw.Stop();

            var estimatedPromptTokens = query.Length / 4;
            var estimatedCompletionTokens = (outputJson.Length) / 4;
            _costTracker.RecordCall(estimatedPromptTokens, estimatedCompletionTokens, sw.Elapsed);

            return new StepExecutionResult
            {
                Status = StepExecutionStatus.Completed,
                Output = outputJson,
                Duration = sw.Elapsed
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            sw.Stop();
            _logger.LogError(ex, "Retrieval step '{StepName}' failed", step.Name);

            return new StepExecutionResult
            {
                Status = StepExecutionStatus.Failed,
                ErrorMessage = $"Retrieval failed: {ex.Message}",
                Duration = sw.Elapsed
            };
        }
    }

    private async Task<string> ExecuteSingleSourceAsync(
        string query, RetrievalStepConfiguration config, CancellationToken ct)
    {
        _logger.LogDebug("Executing single-source retrieval: query={Query}, topK={TopK}, collection={Collection}, strategy={Strategy}",
            query, config.TopK, config.CollectionName, config.Strategy);

        var context = await _ragOrchestrator.SearchAsync(
            query, config.TopK, config.CollectionName, config.Strategy, ct);

        return JsonSerializer.Serialize(context, SerializerOptions);
    }

    private async Task<string> ExecuteMultiSourceAsync(
        string query, RetrievalStepConfiguration config, CancellationToken ct)
    {
        _logger.LogDebug("Executing multi-source retrieval: query={Query}, topK={TopK}",
            query, config.TopK);

        var classification = await _complexityClassifier.ClassifyAsync(query, ct);

        _logger.LogDebug("Query classified as {Complexity} with {Confidence:P0} confidence",
            classification.Complexity, classification.Confidence);

        var results = await _multiSourceOrchestrator.RetrieveFromAllSourcesAsync(
            query, config.TopK ?? 10, classification.Complexity, ct);

        var output = new
        {
            assembledText = string.Join("\n\n", results.Select(r => r.Chunk.Content)),
            totalTokens = results.Sum(r => r.Chunk.Tokens),
            wasTruncated = false,
            resultCount = results.Count,
            complexity = classification.Complexity.ToString()
        };

        return JsonSerializer.Serialize(output, SerializerOptions);
    }

    private static string ResolveQuery(
        string queryTemplate,
        IReadOnlyDictionary<PlanStepId, string> upstreamOutputs)
    {
        if (upstreamOutputs.Count == 0)
            return queryTemplate;

        var contextParts = upstreamOutputs.Values
            .Where(v => !string.IsNullOrEmpty(v))
            .ToList();

        if (contextParts.Count == 0)
            return queryTemplate;

        return $"{queryTemplate}\n\nAdditional context:\n{string.Join("\n", contextParts)}";
    }
}
