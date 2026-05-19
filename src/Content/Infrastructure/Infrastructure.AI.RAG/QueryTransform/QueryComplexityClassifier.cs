using System.Text.Json;
using Application.AI.Common.Interfaces.RAG;
using Domain.AI.RAG.Enums;
using Domain.AI.RAG.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.RAG.QueryTransform;

/// <summary>
/// LLM-based query complexity classifier that determines the retrieval cost tier.
/// Uses few-shot prompting to classify queries as Trivial, Simple, Moderate, or Complex.
/// Falls back to Moderate on any failure to ensure retrieval safety.
/// </summary>
public sealed class QueryComplexityClassifier : IQueryComplexityClassifier
{
    private readonly IRagModelRouter _modelRouter;
    private readonly ILogger<QueryComplexityClassifier> _logger;

    private const string SystemPrompt = """
        You are a query complexity classifier for a RAG system. Classify the user's query into exactly one complexity tier.

        **Tiers:**
        - **trivial**: General knowledge the LLM can answer without any document retrieval. Common facts, definitions, math, coding syntax.
          Examples: "What is the capital of France?", "What does SOLID stand for?", "Convert 5km to miles"
        - **simple**: Requires looking up a single fact or section from the knowledge base. One retrieval pass suffices.
          Examples: "What chunking strategies are configured?", "What is the default topK?", "How is the reranker registered?"
        - **moderate**: Requires cross-referencing multiple sections or comparing concepts. Needs hybrid retrieval + quality evaluation.
          Examples: "Compare CRAG and Self-RAG approaches", "How does the feedback scorer interact with the reranker?", "What are the tradeoffs between Azure and FAISS vector stores?"
        - **complex**: Requires synthesizing information across multiple documents, multi-hop reasoning, or iterative retrieval.
          Examples: "Based on the architecture and deployment docs, what changes support multi-tenant GraphRAG?", "Trace the full execution path from tool call to assembled context and identify all failure modes"

        Respond with JSON only: {"complexity": "trivial|simple|moderate|complex", "confidence": 0.0-1.0, "reasoning": "brief explanation"}
        """;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Initializes a new instance of the <see cref="QueryComplexityClassifier"/> class.
    /// </summary>
    /// <param name="modelRouter">Model router for selecting the appropriate tier chat client.</param>
    /// <param name="logger">Logger for recording classification outcomes and failures.</param>
    public QueryComplexityClassifier(
        IRagModelRouter modelRouter,
        ILogger<QueryComplexityClassifier> logger)
    {
        _modelRouter = modelRouter;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<ComplexityClassification> ClassifyAsync(
        string query,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var client = _modelRouter.GetClientForOperation("complexity_classification");
            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, SystemPrompt),
                new(ChatRole.User, query),
            };

            var response = await client.GetResponseAsync(messages, cancellationToken: cancellationToken);
            var content = response.Text?.Trim() ?? string.Empty;

            return ParseClassification(content);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Complexity classification failed for query, falling back to Moderate");
            return CreateFallback();
        }
    }

    private ComplexityClassification ParseClassification(string content)
    {
        try
        {
            var jsonStart = content.IndexOf('{');
            var jsonEnd = content.LastIndexOf('}');
            if (jsonStart < 0 || jsonEnd < 0 || jsonEnd <= jsonStart)
                return CreateFallback();

            var json = content[jsonStart..(jsonEnd + 1)];
            var dto = JsonSerializer.Deserialize<ClassificationDto>(json, JsonOptions);
            if (dto is null)
                return CreateFallback();

            var complexity = dto.Complexity?.ToLowerInvariant() switch
            {
                "trivial" => QueryComplexity.Trivial,
                "simple" => QueryComplexity.Simple,
                "moderate" => QueryComplexity.Moderate,
                "complex" => QueryComplexity.Complex,
                _ => QueryComplexity.Moderate,
            };

            return new ComplexityClassification
            {
                Complexity = complexity,
                Confidence = Math.Clamp(dto.Confidence, 0.0, 1.0),
                Reasoning = dto.Reasoning,
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse complexity classification JSON, falling back to Moderate");
            return CreateFallback();
        }
    }

    private static ComplexityClassification CreateFallback()
        => new()
        {
            Complexity = QueryComplexity.Moderate,
            Confidence = 0.5,
            Reasoning = "Classification failed; defaulting to Moderate for safety.",
        };

    /// <summary>DTO for deserializing the LLM complexity classification JSON response.</summary>
    private sealed record ClassificationDto
    {
        public string? Complexity { get; init; }
        public double Confidence { get; init; }
        public string? Reasoning { get; init; }
    }
}
