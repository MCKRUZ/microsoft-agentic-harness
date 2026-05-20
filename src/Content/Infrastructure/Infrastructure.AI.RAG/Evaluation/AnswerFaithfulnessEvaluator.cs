using System.Text.Json;
using Application.AI.Common.Interfaces.RAG;
using Domain.AI.RAG.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.RAG.Evaluation;

/// <summary>
/// LLM-based evaluator that checks whether an assembled answer is faithful to the
/// retrieved context. Decomposes the answer into individual claims, verifies each
/// claim against the supporting context, and identifies hallucinated claims that
/// are not grounded in the source material.
/// </summary>
/// <remarks>
/// <para>
/// The evaluator uses claim-level decomposition rather than holistic scoring to
/// provide actionable feedback: specific hallucinated claims can be removed or
/// flagged, enabling targeted corrective action rather than blanket re-retrieval.
/// </para>
/// <para>
/// On any LLM failure, the evaluator returns a conservative unfaithful result
/// (score 0.0) as a fail-safe, triggering corrective action.
/// </para>
/// </remarks>
public sealed class AnswerFaithfulnessEvaluator : IAnswerFaithfulnessEvaluator
{
    private readonly IRagModelRouter _modelRouter;
    private readonly ILogger<AnswerFaithfulnessEvaluator> _logger;

    private const string SystemPrompt = """
        You are a faithfulness evaluator for a RAG (Retrieval-Augmented Generation) system.
        Given an answer and the retrieved context chunks it was generated from, evaluate whether
        the answer is faithful to the context.

        **Your task:**
        1. Decompose the answer into individual factual claims.
        2. For each claim, check if it is supported by the retrieved context.
        3. Classify each claim as "supported" or "hallucinated".
        4. Compute an overall faithfulness score (proportion of supported claims).

        **Scoring:**
        - 1.0: Every claim is directly supported by the context.
        - 0.7-0.99: Most claims supported, minor unsupported details.
        - 0.4-0.69: Mix of supported and unsupported claims.
        - 0.0-0.39: Most claims are not supported by the context.

        **Important:**
        - A claim is hallucinated if it states a specific fact not found in any context chunk.
        - Generic/obvious statements (e.g., "this is important") are not claims and should be ignored.
        - If a claim contradicts the context, it is hallucinated.
        - If a claim is a reasonable inference from the context, it is supported.

        Respond with JSON only:
        {
            "is_faithful": true/false,
            "score": 0.0-1.0,
            "supported_claims": ["claim 1", "claim 2"],
            "hallucinated_claims": ["claim X"],
            "reasoning": "brief explanation"
        }
        """;

    /// <summary>
    /// Initializes a new instance of the <see cref="AnswerFaithfulnessEvaluator"/> class.
    /// </summary>
    /// <param name="modelRouter">Model router for resolving the LLM client.</param>
    /// <param name="logger">Logger for evaluation diagnostics.</param>
    public AnswerFaithfulnessEvaluator(
        IRagModelRouter modelRouter,
        ILogger<AnswerFaithfulnessEvaluator> logger)
    {
        _modelRouter = modelRouter;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<FaithfulnessEvaluation> EvaluateAsync(
        string answer,
        IReadOnlyList<RerankedResult> supportingContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(answer);
        cancellationToken.ThrowIfCancellationRequested();

        if (supportingContext.Count == 0)
        {
            _logger.LogDebug("No supporting context for faithfulness evaluation; returning unfaithful");
            return new FaithfulnessEvaluation
            {
                IsFaithful = false,
                Score = 0.0,
                HallucinatedClaims = [],
                SupportedClaims = [],
                Reasoning = "No supporting context available to verify faithfulness.",
            };
        }

        try
        {
            var client = _modelRouter.GetClientForOperation("faithfulness_evaluation");

            var contextText = string.Join("\n---\n", supportingContext.Select((r, i) =>
                $"[Chunk {i + 1}] (rerank score: {r.RerankScore:F2})\n{r.RetrievalResult.Chunk.Content}"));

            var userPrompt = $"""
                **Answer to evaluate:**
                {answer}

                **Retrieved context chunks:**
                {contextText}

                Evaluate whether the answer is faithful to the context.
                """;

            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, SystemPrompt),
                new(ChatRole.User, userPrompt),
            };

            var response = await client.GetResponseAsync(messages, cancellationToken: cancellationToken);
            var content = response.Text?.Trim() ?? string.Empty;

            return ParseEvaluation(content);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Faithfulness evaluation failed, returning conservative unfaithful result");
            return CreateFallback();
        }
    }

    private FaithfulnessEvaluation ParseEvaluation(string content)
    {
        try
        {
            var jsonStart = content.IndexOf('{');
            var jsonEnd = content.LastIndexOf('}');
            if (jsonStart < 0 || jsonEnd < 0 || jsonEnd <= jsonStart)
                return CreateFallback();

            var json = content[jsonStart..(jsonEnd + 1)];
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var isFaithful = root.TryGetProperty("is_faithful", out var faithfulProp)
                && faithfulProp.GetBoolean();
            var score = root.TryGetProperty("score", out var scoreProp)
                ? Math.Clamp(scoreProp.GetDouble(), 0.0, 1.0)
                : 0.0;
            var reasoning = root.TryGetProperty("reasoning", out var reasonProp)
                ? reasonProp.GetString()
                : null;

            var supportedClaims = ParseStringArray(root, "supported_claims");
            var hallucinatedClaims = ParseStringArray(root, "hallucinated_claims");

            return new FaithfulnessEvaluation
            {
                IsFaithful = isFaithful,
                Score = score,
                SupportedClaims = supportedClaims,
                HallucinatedClaims = hallucinatedClaims,
                Reasoning = reasoning,
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse faithfulness evaluation JSON, returning fallback");
            return CreateFallback();
        }
    }

    private static IReadOnlyList<string> ParseStringArray(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var arrayProp)
            || arrayProp.ValueKind != JsonValueKind.Array)
            return [];

        var items = new List<string>();
        foreach (var element in arrayProp.EnumerateArray())
        {
            var text = element.GetString();
            if (!string.IsNullOrWhiteSpace(text))
                items.Add(text);
        }
        return items;
    }

    private static FaithfulnessEvaluation CreateFallback() =>
        new()
        {
            IsFaithful = false,
            Score = 0.0,
            HallucinatedClaims = [],
            SupportedClaims = [],
            Reasoning = "Faithfulness evaluation failed; returning conservative unfaithful result.",
        };
}
