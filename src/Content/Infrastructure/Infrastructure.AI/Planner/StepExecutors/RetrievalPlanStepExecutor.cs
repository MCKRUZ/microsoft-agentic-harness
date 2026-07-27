using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.Planner;
using Application.AI.Common.Interfaces.RAG;
using Application.AI.Common.Interfaces.Routing;
using Domain.AI.Routing.Models;
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
/// <remarks>
/// Retrieval is authorized as the well-known capability <see cref="PlanCapabilities.Retrieval"/>
/// through <see cref="IToolInvocationGovernor"/> — the identical choke point
/// <c>ToolUseStepExecutor</c> uses. Routing through the governor rather than testing the envelope's
/// allowlist directly is what subjects retrieval to the <em>whole</em> chain: the envelope's
/// autonomy-ceiling baseline (a granted name under a Restricted or Supervised ceiling still resolves
/// to Ask, which the governor blocks), declarative policy, audit, the governance trace, and
/// denial-rate accounting. With no ambient envelope and per-invocation enforcement off the governor
/// is a pure pass-through, so direct in-process <c>IPlanExecutor</c> callers are unchanged.
/// </remarks>
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
    private readonly ITaskComplexityClassifier _complexityClassifier;
    private readonly IRetrievalCostTracker _costTracker;
    private readonly IPlanProgressNotifier _notifier;
    private readonly IToolInvocationGovernor _toolInvocationGovernor;
    private readonly PlanExecutionContext _executionContext;
    private readonly ILogger<RetrievalPlanStepExecutor> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="RetrievalPlanStepExecutor"/>.
    /// </summary>
    /// <param name="ragOrchestrator">Single-source RAG pipeline orchestrator.</param>
    /// <param name="multiSourceOrchestrator">Multi-source orchestrator for fan-out retrieval.</param>
    /// <param name="complexityClassifier">Task complexity classifier for multi-source routing.</param>
    /// <param name="costTracker">Tracks token usage and latency per retrieval call.</param>
    /// <param name="notifier">Plan progress notifier for real-time status updates.</param>
    /// <param name="toolInvocationGovernor">Authorizes retrieval against the ambient capability envelope.</param>
    /// <param name="executionContext">Current plan execution context with depth tracking.</param>
    /// <param name="logger">Logger instance.</param>
    public RetrievalPlanStepExecutor(
        IRagOrchestrator ragOrchestrator,
        IMultiSourceOrchestrator multiSourceOrchestrator,
        ITaskComplexityClassifier complexityClassifier,
        IRetrievalCostTracker costTracker,
        IPlanProgressNotifier notifier,
        IToolInvocationGovernor toolInvocationGovernor,
        PlanExecutionContext executionContext,
        ILogger<RetrievalPlanStepExecutor> logger)
    {
        _ragOrchestrator = ragOrchestrator;
        _multiSourceOrchestrator = multiSourceOrchestrator;
        _complexityClassifier = complexityClassifier;
        _costTracker = costTracker;
        _notifier = notifier;
        _toolInvocationGovernor = toolInvocationGovernor;
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

        var decision = await _toolInvocationGovernor.AuthorizeAsync(PlanCapabilities.Retrieval, ct);
        if (!decision.IsAllowed)
        {
            _logger.LogWarning(
                "Retrieval step {Step} denied by invocation governor", step.Name);
            return new StepExecutionResult
            {
                Status = StepExecutionStatus.Failed,
                Duration = TimeSpan.Zero,
                ErrorMessage = decision.DeniedMessage
                    ?? Domain.AI.Governance.GovernanceDenials.NotPermitted(PlanCapabilities.Retrieval),
                IsPolicyDenial = true
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
            // Full detail stays in the structured log; only a stable code is persisted onto the step,
            // because step error state is returned to callers and retrieval-stack exceptions carry
            // store endpoints and credentials.
            _logger.LogError(ex, "Retrieval step '{StepName}' failed", step.Name);

            return new StepExecutionResult
            {
                Status = StepExecutionStatus.Failed,
                ErrorMessage = PlanStepErrors.RetrievalFailed,
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

        var classification = await _complexityClassifier.ClassifyAsync(
            new AgentTurnContext { ConversationId = "planner-retrieval", UserMessage = query, TurnNumber = 1 },
            ct);

        _logger.LogDebug("Query classified as {Complexity} with {Confidence:P0} confidence",
            classification.Complexity, classification.Confidence);

        var results = await _multiSourceOrchestrator.RetrieveFromAllSourcesAsync(
            query, config.TopK ?? 10, classification.Complexity, collectionName: null, ct);

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
