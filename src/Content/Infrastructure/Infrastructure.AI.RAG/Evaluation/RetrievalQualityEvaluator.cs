using System.Globalization;
using Application.AI.Common.Interfaces.RAG;
using Domain.AI.RAG.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.RAG.Evaluation;

/// <summary>
/// Evaluates retrieval quality using Ragas-inspired metrics via LLM judges.
/// Each metric (context precision, recall, faithfulness, answer relevancy) is
/// assessed by a separate LLM call for independent, explainable scoring.
/// </summary>
/// <remarks>
/// <para>
/// Metrics are evaluated in parallel via <see cref="Task.WhenAll"/> for lower
/// latency. Context recall is only computed when ground-truth is provided;
/// otherwise it is set to <c>-1.0</c> as a sentinel.
/// </para>
/// <para>
/// On any LLM failure, the evaluator returns a zero-score fallback report
/// with a diagnostic message rather than throwing, ensuring downstream
/// consumers always receive a valid <see cref="RetrievalQualityReport"/>.
/// </para>
/// </remarks>
public sealed class RetrievalQualityEvaluator : IRetrievalQualityEvaluator
{
    private const string OperationName = "quality_evaluation";

    private const string PrecisionPrompt = """
        You are an impartial judge evaluating retrieval quality.

        Given the following user query and retrieved context chunks, evaluate what fraction
        of the retrieved chunks are actually relevant to answering the query.

        User query: {0}

        Retrieved context:
        {1}

        Respond with ONLY a single decimal number between 0.0 and 1.0 representing the
        fraction of chunks that are relevant. 1.0 means all chunks are relevant, 0.0 means
        none are relevant.
        """;

    private const string RecallPrompt = """
        You are an impartial judge evaluating retrieval completeness.

        Given the following user query, the ground-truth answer, and the retrieved context,
        evaluate what fraction of the information in the ground-truth answer is captured
        in the retrieved context.

        User query: {0}
        Ground-truth answer: {1}

        Retrieved context:
        {2}

        Respond with ONLY a single decimal number between 0.0 and 1.0. 1.0 means all
        ground-truth information is present in the context, 0.0 means none is.
        """;

    private const string FaithfulnessPrompt = """
        You are an impartial judge evaluating answer faithfulness.

        Given the following user query, the generated answer, and the retrieved context,
        evaluate whether every claim in the answer is supported by the retrieved context.

        User query: {0}
        Generated answer: {1}

        Retrieved context:
        {2}

        Respond with ONLY a single decimal number between 0.0 and 1.0. 1.0 means every
        claim is fully supported by the context, 0.0 means the answer is entirely
        unsupported (hallucinated).
        """;

    private const string RelevancyPrompt = """
        You are an impartial judge evaluating answer relevancy.

        Given the following user query and the generated answer, evaluate how well the
        answer addresses the original question.

        User query: {0}
        Generated answer: {1}

        Respond with ONLY a single decimal number between 0.0 and 1.0. 1.0 means the
        answer perfectly addresses the question, 0.0 means it is completely off-topic.
        """;

    private readonly IRagModelRouter _modelRouter;
    private readonly ILogger<RetrievalQualityEvaluator> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="RetrievalQualityEvaluator"/> class.
    /// </summary>
    /// <param name="modelRouter">Model router for resolving the LLM client used as judge.</param>
    /// <param name="logger">Logger for evaluation diagnostics.</param>
    public RetrievalQualityEvaluator(
        IRagModelRouter modelRouter,
        ILogger<RetrievalQualityEvaluator> logger)
    {
        ArgumentNullException.ThrowIfNull(modelRouter);
        ArgumentNullException.ThrowIfNull(logger);

        _modelRouter = modelRouter;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<RetrievalQualityReport> EvaluateAsync(
        string query,
        string answer,
        IReadOnlyList<RerankedResult> context,
        string? groundTruth = null,
        CancellationToken cancellationToken = default)
    {
        var contextText = FormatContext(context);

        try
        {
            var precisionTask = EvaluateMetricAsync(
                string.Format(PrecisionPrompt, query, contextText), cancellationToken);
            var faithfulnessTask = EvaluateMetricAsync(
                string.Format(FaithfulnessPrompt, query, answer, contextText), cancellationToken);
            var relevancyTask = EvaluateMetricAsync(
                string.Format(RelevancyPrompt, query, answer), cancellationToken);

            Task<double> recallTask;
            if (groundTruth is not null)
            {
                recallTask = EvaluateMetricAsync(
                    string.Format(RecallPrompt, query, groundTruth, contextText), cancellationToken);
            }
            else
            {
                recallTask = Task.FromResult(-1.0);
            }

            await Task.WhenAll(precisionTask, recallTask, faithfulnessTask, relevancyTask);

            var precision = await precisionTask;
            var recall = await recallTask;
            var faithfulness = await faithfulnessTask;
            var relevancy = await relevancyTask;

            var overallScore = CalculateOverallScore(precision, recall, faithfulness, relevancy);

            _logger.LogInformation(
                "Quality evaluation: Precision={Precision:F2}, Recall={Recall:F2}, " +
                "Faithfulness={Faithfulness:F2}, Relevancy={Relevancy:F2}, Overall={Overall:F2}",
                precision, recall, faithfulness, relevancy, overallScore);

            return new RetrievalQualityReport
            {
                ContextPrecision = precision,
                ContextRecall = recall,
                Faithfulness = faithfulness,
                AnswerRelevancy = relevancy,
                OverallScore = overallScore,
                Reasoning = $"Precision={precision:F2}, Recall={recall:F2}, " +
                            $"Faithfulness={faithfulness:F2}, Relevancy={relevancy:F2}",
                EvaluatedAt = DateTimeOffset.UtcNow
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Quality evaluation failed: {Message}", ex.Message);
            return CreateFallbackReport(ex.Message);
        }
    }

    private async Task<double> EvaluateMetricAsync(string prompt, CancellationToken cancellationToken)
    {
        var client = _modelRouter.GetClientForOperation(OperationName);

        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, prompt)
        };

        var options = new ChatOptions { Temperature = 0.0f, MaxOutputTokens = 10 };
        var response = await client.GetResponseAsync(messages, options, cancellationToken);

        var responseText = response.Text?.Trim() ?? "0.0";

        if (double.TryParse(responseText, CultureInfo.InvariantCulture, out var score))
            return Math.Clamp(score, 0.0, 1.0);

        _logger.LogWarning("Could not parse metric score from LLM response: '{Response}'", responseText);
        return 0.0;
    }

    private static double CalculateOverallScore(
        double precision, double recall, double faithfulness, double relevancy)
    {
        if (recall < 0)
        {
            return (precision * 0.33) + (faithfulness * 0.40) + (relevancy * 0.27);
        }

        return (precision * 0.25) + (recall * 0.25) + (faithfulness * 0.30) + (relevancy * 0.20);
    }

    private static string FormatContext(IReadOnlyList<RerankedResult> context)
    {
        return string.Join("\n---\n", context.Select((r, i) =>
            $"[Chunk {i + 1}] (score: {r.RerankScore:F2})\n{r.RetrievalResult.Chunk.Content}"));
    }

    private static RetrievalQualityReport CreateFallbackReport(string errorMessage) => new()
    {
        ContextPrecision = 0.0,
        ContextRecall = -1.0,
        Faithfulness = 0.0,
        AnswerRelevancy = 0.0,
        OverallScore = 0.0,
        Reasoning = $"Quality evaluation failed: {errorMessage}",
        EvaluatedAt = DateTimeOffset.UtcNow
    };
}
