using System.Diagnostics;
using System.Text.Json;
using Application.AI.Common.Interfaces.RAG;
using Application.AI.Common.Interfaces.Routing;
using Application.AI.Common.Prompts.Exceptions;
using Application.AI.Common.Prompts.Interfaces;
using Domain.Common.Helpers;
using Domain.AI.Prompts;
using Domain.AI.RAG.Enums;
using Domain.AI.RAG.Models;
using Domain.AI.Telemetry.Conventions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.RAG.QueryTransform;

/// <summary>
/// Classifies incoming queries using few-shot LLM prompting to determine the
/// optimal retrieval strategy. Resolves its classification prompt from the versioned
/// <see cref="IPromptRegistry"/> (<c>query-classifier</c>), renders via
/// <see cref="IPromptRenderer"/>, and stamps usage telemetry via
/// <see cref="IPromptUsageRecorder"/>. Sends a structured prompt with examples of each
/// <see cref="QueryType"/> and parses the JSON response into a
/// <see cref="QueryClassification"/> record. Uses an economy-tier model via
/// <see cref="IModelRouter"/> since classification is latency-sensitive
/// and does not require a high-capability model.
/// </summary>
/// <remarks>
/// <para>
/// When the prompt cannot be resolved, the LLM response cannot be parsed, or confidence
/// falls below 0.5, the classifier returns a conservative default of
/// <see cref="QueryType.SimpleLookup"/> with
/// <see cref="RetrievalStrategy.HybridVectorBm25"/>.
/// </para>
/// </remarks>
public sealed class LlmQueryClassifier : IQueryClassifier
{
    private const string PromptName = "query-classifier";
    private const string MetricKey = "query_classification";

    private static readonly ActivitySource ActivitySource = new("Infrastructure.AI.RAG.QueryTransform");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly IReadOnlyDictionary<QueryType, RetrievalStrategy> DefaultStrategyMap =
        new Dictionary<QueryType, RetrievalStrategy>
        {
            [QueryType.SimpleLookup] = RetrievalStrategy.HybridVectorBm25,
            [QueryType.MultiHop] = RetrievalStrategy.MultiQueryFusion,
            [QueryType.GlobalThematic] = RetrievalStrategy.GraphRag,
            [QueryType.Comparative] = RetrievalStrategy.MultiQueryFusion,
            [QueryType.Adversarial] = RetrievalStrategy.HybridVectorBm25
        };

    private readonly IModelRouter _modelRouter;
    private readonly IPromptRegistry _promptRegistry;
    private readonly IPromptRenderer _promptRenderer;
    private readonly IPromptUsageRecorder _usageRecorder;
    private readonly ILogger<LlmQueryClassifier> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="LlmQueryClassifier"/> class.
    /// </summary>
    /// <param name="modelRouter">Model router for selecting an economy-tier chat client.</param>
    /// <param name="promptRegistry">Versioned prompt registry; resolves the classifier template.</param>
    /// <param name="promptRenderer">Renders the resolved template with variable substitution (Scriban).</param>
    /// <param name="usageRecorder">Stamps OTel / persists which prompt version was used per query.</param>
    /// <param name="logger">Logger for recording classification outcomes and failures.</param>
    public LlmQueryClassifier(
        IModelRouter modelRouter,
        IPromptRegistry promptRegistry,
        IPromptRenderer promptRenderer,
        IPromptUsageRecorder usageRecorder,
        ILogger<LlmQueryClassifier> logger)
    {
        ArgumentNullException.ThrowIfNull(modelRouter);
        ArgumentNullException.ThrowIfNull(promptRegistry);
        ArgumentNullException.ThrowIfNull(promptRenderer);
        ArgumentNullException.ThrowIfNull(usageRecorder);
        ArgumentNullException.ThrowIfNull(logger);

        _modelRouter = modelRouter;
        _promptRegistry = promptRegistry;
        _promptRenderer = promptRenderer;
        _usageRecorder = usageRecorder;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<QueryClassification> ClassifyAsync(
        string query,
        CancellationToken cancellationToken = default)
    {
        using var activity = ActivitySource.StartActivity("rag.query_classification");

        activity?.SetTag(RagConventions.ModelOperation, "query_classification");

        PromptDescriptor descriptor;
        try
        {
            descriptor = await _promptRegistry.GetLatestAsync(PromptName, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is KeyNotFoundException or PromptRegistryUnavailableException)
        {
            _logger.LogWarning(ex,
                "Could not resolve prompt '{Prompt}'; falling back to SimpleLookup/HybridVectorBm25",
                PromptName);
            return TagFallback(activity, "Prompt unavailable: " + ex.Message);
        }

        try
        {
            var classifyDecision = await _modelRouter.RouteOperationAsync("query_classification", cancellationToken);
            var chatClient = classifyDecision.Client;
            activity?.SetTag(RagConventions.ModelTier, classifyDecision.SelectedTier.ToString().ToLowerInvariant());

            var rendered = await _promptRenderer.RenderAsync(
                descriptor,
                new Dictionary<string, object?> { ["query"] = query },
                cancellationToken).ConfigureAwait(false);

            await _usageRecorder.RecordAsync(
                descriptor,
                new PromptUsageContext { MetricKey = MetricKey },
                cancellationToken).ConfigureAwait(false);

            var response = await chatClient.GetResponseAsync(rendered.Body, cancellationToken: cancellationToken);
            var responseText = response.Text?.Trim() ?? string.Empty;

            var classification = ParseClassificationResponse(responseText);

            activity?.SetTag(RagConventions.QueryType, ToSnakeCase(classification.Type));
            activity?.SetTag(RagConventions.RetrievalStrategy, ToSnakeCase(classification.Strategy));

            _logger.LogInformation(
                "Query classified as {QueryType} with strategy {Strategy} (confidence: {Confidence:F2})",
                classification.Type, classification.Strategy, classification.Confidence);

            return classification;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Query classification failed; falling back to SimpleLookup/HybridVectorBm25");
            return TagFallback(activity, "Classification failed: " + ex.Message);
        }
    }

    private static QueryClassification TagFallback(Activity? activity, string reasoning)
    {
        activity?.SetTag(RagConventions.QueryType, RagConventions.QueryTypeValues.SimpleLookup);
        activity?.SetTag(RagConventions.RetrievalStrategy, RagConventions.StrategyValues.HybridVectorBm25);
        return CreateFallback(reasoning);
    }

    /// <summary>
    /// Parses the LLM JSON response into a <see cref="QueryClassification"/>.
    /// Falls back to conservative defaults when parsing fails.
    /// </summary>
    private QueryClassification ParseClassificationResponse(string responseText)
    {
        if (!Application.AI.Common.Json.LlmJsonResponseParser.TryParseObject<ClassificationDto>(responseText, JsonOptions, out var dto)
            || dto is null)
        {
            _logger.LogWarning("Failed to parse classification JSON: {Response}", responseText);
            return CreateFallback("JSON parse failure");
        }

        // Name-only: dto.Type is whatever the model emitted. A numeric answer would parse to a
        // QueryType that is not a member, then miss every entry in DefaultStrategyMap and silently
        // take the map's fallback strategy instead of the logged SimpleLookup default below.
        if (!EnumNameHelper.TryParseName<QueryType>(dto.Type, out var queryType))
        {
            _logger.LogWarning("Unknown query type '{Type}' in classification response; defaulting to SimpleLookup", dto.Type);
            queryType = QueryType.SimpleLookup;
        }

        var strategy = DefaultStrategyMap.GetValueOrDefault(queryType, RetrievalStrategy.HybridVectorBm25);
        var confidence = Math.Clamp(dto.Confidence, 0.0, 1.0);

        return new QueryClassification
        {
            Type = queryType,
            Strategy = strategy,
            Confidence = confidence,
            Reasoning = dto.Reasoning
        };
    }

    private static QueryClassification CreateFallback(string reasoning) => new()
    {
        Type = QueryType.SimpleLookup,
        Strategy = RetrievalStrategy.HybridVectorBm25,
        Confidence = 0.5,
        Reasoning = reasoning
    };

    private static string ToSnakeCase(QueryType type) => type switch
    {
        QueryType.SimpleLookup => RagConventions.QueryTypeValues.SimpleLookup,
        QueryType.MultiHop => RagConventions.QueryTypeValues.MultiHop,
        QueryType.GlobalThematic => RagConventions.QueryTypeValues.GlobalThematic,
        QueryType.Comparative => RagConventions.QueryTypeValues.Comparative,
        QueryType.Adversarial => RagConventions.QueryTypeValues.Adversarial,
        _ => type.ToString().ToLowerInvariant()
    };

    private static string ToSnakeCase(RetrievalStrategy strategy) => strategy switch
    {
        RetrievalStrategy.HybridVectorBm25 => RagConventions.StrategyValues.HybridVectorBm25,
        RetrievalStrategy.GraphRag => RagConventions.StrategyValues.GraphRag,
        RetrievalStrategy.RaptorTree => RagConventions.StrategyValues.RaptorTree,
        RetrievalStrategy.MultiQueryFusion => RagConventions.StrategyValues.MultiQueryFusion,
        _ => strategy.ToString().ToLowerInvariant()
    };

    /// <summary>DTO for deserializing the LLM classification JSON response.</summary>
    private sealed record ClassificationDto
    {
        public string Type { get; init; } = string.Empty;
        public string Strategy { get; init; } = string.Empty;
        public double Confidence { get; init; }
        public string? Reasoning { get; init; }
    }
}
