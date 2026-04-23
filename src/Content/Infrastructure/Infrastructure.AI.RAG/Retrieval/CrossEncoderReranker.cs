using System.Text.Json;
using Application.AI.Common.Interfaces.RAG;
using Domain.AI.RAG.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.RAG.Retrieval;

/// <summary>
/// Cross-encoder reranker that uses an LLM to score (query, chunk) pairs for
/// relevance. Routes to an appropriate model via <see cref="IRagModelRouter"/>
/// using the <c>"reranking"</c> operation. Batches scoring prompts for efficiency.
/// Registered as keyed service <c>"cross_encoder"</c>.
/// </summary>
/// <remarks>
/// <para>
/// This is more expensive than <see cref="AzureSemanticReranker"/> but works with
/// any LLM backend. Each chunk is scored independently with a structured prompt
/// asking for a relevance score between 0.0 and 1.0. Results are re-sorted by
/// the LLM-assigned score.
/// </para>
/// <para>
/// Batching strategy: chunks are scored in parallel batches of
/// <see cref="BatchSize"/> to balance throughput against rate limits.
/// </para>
/// </remarks>
public sealed class CrossEncoderReranker : IReranker
{
    private const int BatchSize = 10;

    private readonly IRagModelRouter _modelRouter;
    private readonly ILogger<CrossEncoderReranker> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="CrossEncoderReranker"/> class.
    /// </summary>
    /// <param name="modelRouter">The model router for selecting the reranking model.</param>
    /// <param name="logger">The logger instance.</param>
    public CrossEncoderReranker(
        IRagModelRouter modelRouter,
        ILogger<CrossEncoderReranker> logger)
    {
        _modelRouter = modelRouter;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RerankedResult>> RerankAsync(
        string query,
        IReadOnlyList<RetrievalResult> results,
        int topK,
        CancellationToken cancellationToken = default)
    {
        if (results.Count == 0) return [];

        var client = _modelRouter.GetClientForOperation("reranking");
        var scoredResults = new List<(RetrievalResult Result, int OriginalRank, double Score)>();

        var batches = results
            .Select((r, i) => (Result: r, OriginalRank: i + 1))
            .Chunk(BatchSize);

        foreach (var batch in batches)
        {
            var tasks = batch.Select(item =>
                ScoreChunkAsync(client, query, item.Result, item.OriginalRank, cancellationToken));

            var batchResults = await Task.WhenAll(tasks);
            scoredResults.AddRange(batchResults);
        }

        var reranked = scoredResults
            .OrderByDescending(s => s.Score)
            .Take(topK)
            .Select((s, i) => new RerankedResult
            {
                RetrievalResult = s.Result,
                RerankScore = s.Score,
                OriginalRank = s.OriginalRank,
                RerankRank = i + 1,
            })
            .ToList();

        _logger.LogDebug(
            "Cross-encoder reranker scored {Input} candidates, returned {Output} results",
            results.Count, reranked.Count);

        return reranked;
    }

    private async Task<(RetrievalResult Result, int OriginalRank, double Score)> ScoreChunkAsync(
        IChatClient client,
        string query,
        RetrievalResult result,
        int originalRank,
        CancellationToken cancellationToken)
    {
        try
        {
            var prompt = BuildScoringPrompt(query, result.Chunk.Content);

            var response = await client.GetResponseAsync(
                [new ChatMessage(ChatRole.User, prompt)],
                cancellationToken: cancellationToken);

            var score = ParseScore(response.Text);

            return (result, originalRank, score);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to score chunk {ChunkId}, assigning fused score as fallback",
                result.Chunk.Id);

            return (result, originalRank, result.FusedScore);
        }
    }

    private static string BuildScoringPrompt(string query, string content) =>
        $"""
        Rate the relevance of the following document to the query on a scale of 0.0 to 1.0.
        Respond with ONLY a decimal number between 0.0 and 1.0.

        Query: {query}

        Document:
        {content[..Math.Min(content.Length, 2000)]}

        Relevance score:
        """;

    /// <summary>
    /// Parses the LLM response to extract a relevance score. Falls back to 0.0
    /// if the response cannot be parsed as a valid score.
    /// </summary>
    private static double ParseScore(string? response)
    {
        if (string.IsNullOrWhiteSpace(response)) return 0.0;

        var trimmed = response.Trim();

        if (double.TryParse(trimmed, out var score))
        {
            return Math.Clamp(score, 0.0, 1.0);
        }

        foreach (var token in trimmed.Split(' ', '\n', '\r', '\t'))
        {
            if (double.TryParse(token, out var tokenScore))
            {
                return Math.Clamp(tokenScore, 0.0, 1.0);
            }
        }

        return 0.0;
    }
}
