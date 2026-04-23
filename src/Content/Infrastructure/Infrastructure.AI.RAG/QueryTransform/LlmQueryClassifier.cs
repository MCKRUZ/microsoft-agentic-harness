using System.Diagnostics;
using System.Text.Json;
using Application.AI.Common.Interfaces.RAG;
using Domain.AI.RAG.Enums;
using Domain.AI.RAG.Models;
using Domain.AI.Telemetry.Conventions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.RAG.QueryTransform;

/// <summary>
/// Classifies incoming queries using few-shot LLM prompting to determine the
/// optimal retrieval strategy. Sends a structured prompt with examples of each
/// <see cref="QueryType"/> and parses the JSON response into a
/// <see cref="QueryClassification"/> record. Uses an economy-tier model via
/// <see cref="IRagModelRouter"/> since classification is latency-sensitive
/// and does not require a high-capability model.
/// </summary>
/// <remarks>
/// <para>
/// When the LLM response cannot be parsed or confidence falls below 0.5,
/// the classifier returns a conservative default of
/// <see cref="QueryType.SimpleLookup"/> with
/// <see cref="RetrievalStrategy.HybridVectorBm25"/>.
/// </para>
/// </remarks>
public sealed class LlmQueryClassifier : IQueryClassifier
{
    private static readonly ActivitySource ActivitySource = new("Infrastructure.AI.RAG.QueryTransform");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private const string ClassificationPrompt = """
        Classify this search query into one of: SimpleLookup, MultiHop, GlobalThematic, Comparative, Adversarial

        Examples:
        - "What was AMD's Q4 revenue?" → SimpleLookup
        - "Compare Intel and AMD's R&D spending trends" → Comparative
        - "What are the main themes across these filings?" → GlobalThematic
        - "What was the revenue in 2022 and how did it affect 2023 margins?" → MultiHop
        - "Why did the CEO resign?" (when no resignation occurred) → Adversarial

        Query: {0}

        Respond with JSON only: {{"type": "...", "strategy": "...", "confidence": 0.0-1.0, "reasoning": "..."}}
        """;

    private static readonly IReadOnlyDictionary<QueryType, RetrievalStrategy> DefaultStrategyMap =
        new Dictionary<QueryType, RetrievalStrategy>
        {
            [QueryType.SimpleLookup] = RetrievalStrategy.HybridVectorBm25,
            [QueryType.MultiHop] = RetrievalStrategy.MultiQueryFusion,
            [QueryType.GlobalThematic] = RetrievalStrategy.GraphRag,
            [QueryType.Comparative] = RetrievalStrategy.MultiQueryFusion,
            [QueryType.Adversarial] = RetrievalStrategy.HybridVectorBm25
        };

    private readonly IRagModelRouter _modelRouter;
    private readonly ILogger<LlmQueryClassifier> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="LlmQueryClassifier"/> class.
    /// </summary>
    /// <param name="modelRouter">Model router for selecting an economy-tier chat client.</param>
    /// <param name="logger">Logger for recording classification outcomes and failures.</param>
    public LlmQueryClassifier(
        IRagModelRouter modelRouter,
        ILogger<LlmQueryClassifier> logger)
    {
        _modelRouter = modelRouter;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<QueryClassification> ClassifyAsync(
        string query,
        CancellationToken cancellationToken = default)
    {
        using var activity = ActivitySource.StartActivity("rag.query_classification");

        var tier = _modelRouter.GetTierForOperation("query_classification");
        activity?.SetTag(RagConventions.ModelTier, tier);
        activity?.SetTag(RagConventions.ModelOperation, "query_classification");

        try
        {
            var chatClient = _modelRouter.GetClientForOperation("query_classification");
            var prompt = string.Format(ClassificationPrompt, query);

            var response = await chatClient.GetResponseAsync(prompt, cancellationToken: cancellationToken);
            var responseText = response.Text?.Trim() ?? string.Empty;

            var classification = ParseClassificationResponse(responseText);

            activity?.SetTag(RagConventions.QueryType, ToSnakeCase(classification.Type));
            activity?.SetTag(RagConventions.RetrievalStrategy, ToSnakeCase(classification.Strategy));

            _logger.LogInformation(
                "Query classified as {QueryType} with strategy {Strategy} (confidence: {Confidence:F2})",
                classification.Type, classification.Strategy, classification.Confidence);

            return classification;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Query classification failed; falling back to SimpleLookup/HybridVectorBm25");

            var fallback = CreateFallback("Classification failed: " + ex.Message);
            activity?.SetTag(RagConventions.QueryType, RagConventions.QueryTypeValues.SimpleLookup);
            activity?.SetTag(RagConventions.RetrievalStrategy, RagConventions.StrategyValues.HybridVectorBm25);
            return fallback;
        }
    }

    /// <summary>
    /// Parses the LLM JSON response into a <see cref="QueryClassification"/>.
    /// Falls back to conservative defaults when parsing fails.
    /// </summary>
    private QueryClassification ParseClassificationResponse(string responseText)
    {
        // Strip markdown code fences if present
        var json = responseText;
        if (json.StartsWith("```"))
        {
            var firstNewline = json.IndexOf('\n');
            var lastFence = json.LastIndexOf("```");
            if (firstNewline >= 0 && lastFence > firstNewline)
                json = json[(firstNewline + 1)..lastFence].Trim();
        }

        ClassificationDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<ClassificationDto>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse classification JSON: {Response}", responseText);
            return CreateFallback("JSON parse failure");
        }

        if (dto is null)
            return CreateFallback("Null deserialization result");

        if (!Enum.TryParse<QueryType>(dto.Type, ignoreCase: true, out var queryType))
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
