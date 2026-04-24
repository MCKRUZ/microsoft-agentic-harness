using System.Text.Json;
using Application.AI.Common.Interfaces.KnowledgeGraph;
using Application.AI.Common.Interfaces.RAG;
using Domain.AI.KnowledgeGraph.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.KnowledgeGraph.Feedback;

/// <summary>
/// LLM-based <see cref="IFeedbackDetector"/> that uses an economy-tier model
/// to detect implicit feedback signals in user messages. Analyzes whether the
/// user is expressing satisfaction, dissatisfaction, or indifference toward
/// the assistant's previous response.
/// </summary>
/// <remarks>
/// Uses <see cref="IRagModelRouter"/> to select a cheap model for feedback
/// detection, keeping per-turn cost minimal. Returns a no-feedback result
/// if the LLM call fails, ensuring the pipeline continues gracefully.
/// </remarks>
public sealed class LlmFeedbackDetector : IFeedbackDetector
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IRagModelRouter _modelRouter;
    private readonly ILogger<LlmFeedbackDetector> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="LlmFeedbackDetector"/> class.
    /// </summary>
    /// <param name="modelRouter">Routes LLM calls to the appropriate model tier.</param>
    /// <param name="logger">Logger for recording detection results.</param>
    public LlmFeedbackDetector(
        IRagModelRouter modelRouter,
        ILogger<LlmFeedbackDetector> logger)
    {
        ArgumentNullException.ThrowIfNull(modelRouter);
        ArgumentNullException.ThrowIfNull(logger);

        _modelRouter = modelRouter;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<FeedbackDetectionResult> DetectFeedbackAsync(
        string userMessage,
        string assistantResponse,
        CancellationToken cancellationToken = default)
    {
        var client = _modelRouter.GetClientForOperation("feedback_detection");
        var prompt = $$"""
            Analyze whether the user's message contains implicit feedback about the assistant's response.

            Assistant's response (truncated):
            {{assistantResponse[..Math.Min(assistantResponse.Length, 500)]}}

            User's message:
            {{userMessage}}

            Return a JSON object:
            {
              "feedbackDetected": true/false,
              "feedbackScore": 1-5 (1=very negative, 3=neutral, 5=very positive),
              "feedbackText": "brief description of the feedback signal",
              "containsFollowupQuestion": true/false
            }

            JSON:
            """;

        try
        {
            var response = await client.GetResponseAsync(prompt, cancellationToken: cancellationToken);
            var json = response.Text ?? "{}";

            var startIndex = json.IndexOf('{');
            var endIndex = json.LastIndexOf('}');
            if (startIndex >= 0 && endIndex > startIndex)
                json = json[startIndex..(endIndex + 1)];

            var parsed = JsonSerializer.Deserialize<FeedbackJson>(json, JsonOptions);
            if (parsed is null || !parsed.FeedbackDetected)
                return NoFeedback();

            _logger.LogDebug(
                "Feedback detected: Score={Score}, Text={Text}",
                parsed.FeedbackScore, parsed.FeedbackText);

            return new FeedbackDetectionResult
            {
                FeedbackDetected = true,
                FeedbackText = parsed.FeedbackText,
                FeedbackScore = parsed.FeedbackScore,
                ContainsFollowupQuestion = parsed.ContainsFollowupQuestion
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Feedback detection failed; treating as no feedback");
            return NoFeedback();
        }
    }

    private static FeedbackDetectionResult NoFeedback() => new()
    {
        FeedbackDetected = false,
        ContainsFollowupQuestion = false
    };

    private sealed record FeedbackJson
    {
        public bool FeedbackDetected { get; init; }
        public int? FeedbackScore { get; init; }
        public string? FeedbackText { get; init; }
        public bool ContainsFollowupQuestion { get; init; }
    }
}
