using System.Text.Json;
using Application.AI.Common.Interfaces.RAG;
using Domain.AI.RAG.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.RAG.Evaluation;

/// <summary>
/// LLM-based evaluator that assesses whether retrieved chunks sufficiently answer
/// a sub-query. Returns a score between 0.0 (completely insufficient) and 1.0
/// (fully sufficient). Returns 0.0 immediately for empty result sets without
/// calling the LLM. Falls back to 0.5 (uncertain) on any LLM failure.
/// </summary>
public sealed class SufficiencyEvaluator : ISufficiencyEvaluator
{
    private readonly IRagModelRouter _modelRouter;
    private readonly ILogger<SufficiencyEvaluator> _logger;

    private const double DefaultScore = 0.5;

    private const string SystemPrompt = """
        You are a retrieval sufficiency evaluator for a RAG system. Given a sub-query and a set of
        retrieved document chunks, assess whether the chunks contain enough information to fully
        answer the sub-query.

        **Scoring guide:**
        - **0.9-1.0**: Chunks directly and completely answer the sub-query with specific details.
        - **0.7-0.89**: Chunks address the main question but may lack minor details.
        - **0.5-0.69**: Chunks are partially relevant but miss key aspects of the question.
        - **0.3-0.49**: Chunks are tangentially related but do not meaningfully answer the question.
        - **0.0-0.29**: Chunks are irrelevant to the sub-query.

        Respond with JSON only: {"score": 0.0-1.0, "reasoning": "brief explanation"}
        """;

    /// <summary>
    /// Initializes a new instance of the <see cref="SufficiencyEvaluator"/> class.
    /// </summary>
    /// <param name="modelRouter">Model router for resolving the LLM client.</param>
    /// <param name="logger">Logger for evaluation diagnostics.</param>
    public SufficiencyEvaluator(
        IRagModelRouter modelRouter,
        ILogger<SufficiencyEvaluator> logger)
    {
        _modelRouter = modelRouter;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<double> EvaluateAsync(
        string subQuery,
        IReadOnlyList<RetrievalResult> results,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (results.Count == 0)
        {
            _logger.LogDebug("No results to evaluate sufficiency for sub-query; returning 0.0");
            return 0.0;
        }

        try
        {
            var client = _modelRouter.GetClientForOperation("sufficiency_evaluation");

            var chunksText = string.Join("\n---\n", results.Select((r, i) =>
                $"[Chunk {i + 1}] (score: {r.FusedScore:F2})\n{r.Chunk.Content}"));

            var userPrompt = $"""
                **Sub-query:** {subQuery}

                **Retrieved chunks:**
                {chunksText}

                Evaluate whether these chunks sufficiently answer the sub-query.
                """;

            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, SystemPrompt),
                new(ChatRole.User, userPrompt),
            };

            var response = await client.GetResponseAsync(messages, cancellationToken: cancellationToken);
            var content = response.Text?.Trim() ?? string.Empty;

            return ParseScore(content);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Sufficiency evaluation failed, returning default score {Score}", DefaultScore);
            return DefaultScore;
        }
    }

    private double ParseScore(string content)
    {
        try
        {
            var jsonStart = content.IndexOf('{');
            var jsonEnd = content.LastIndexOf('}');
            if (jsonStart < 0 || jsonEnd < 0 || jsonEnd <= jsonStart)
                return DefaultScore;

            var json = content[jsonStart..(jsonEnd + 1)];
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("score", out var scoreProp))
            {
                var score = scoreProp.GetDouble();
                return Math.Clamp(score, 0.0, 1.0);
            }

            return DefaultScore;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse sufficiency score, returning default");
            return DefaultScore;
        }
    }
}
